using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record CMakeTargetRef(string BuildDirectory, string Target, string? Configuration);
public sealed record BuiltArtifact(CMakeTargetRef TargetRef, string ExecutablePath);

// Shared invocation for executable and utility targets. Resolving an executable
// is a separate operation: a check/campaign/aggregate target has no binary.
public sealed record CMakeBuildCommand(IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public static CMakeBuildCommand Create(CMakeTargetRef target)
    {
        if (string.IsNullOrWhiteSpace(target.Target) || target.Target.StartsWith('-') || target.Target.Contains('\0'))
            throw new ArgumentException("A literal native target name is required; build options are not target names.");
        target = CMakeConfiguration.Resolve(target);
        var args = new List<string> { "--build", target.BuildDirectory, "--target", target.Target };
        if (!string.IsNullOrWhiteSpace(target.Configuration))
            args.AddRange(["--config", target.Configuration]);
        return new CMakeBuildCommand(args, target.BuildDirectory);
    }
}

// The CMake build graph, not this program, decides HOW the target is built.
public interface ICMakeBuildContext
{
    Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken cancellationToken);
}

public sealed class ExternalCMakeBuildContext : ICMakeBuildContext
{
    public async Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken ct)
    {
        var observed = await CMakeInspection.InspectAsync(target.BuildDirectory, ct);
        target = CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration), observed, target.Target);
        CMakeInspection.RequireTarget(observed, target, executableOnly: true);
        var command = CMakeBuildCommand.Create(target);
        await ProcessRunner.RunAsync(observed.CMakeExecutable ?? "cmake", command.Arguments, command.WorkingDirectory, ct);
        return await CMakeFileApi.ResolveExecutableAsync(target, ct);
    }
}

// Only valid when called by a CMake target with DEPENDS on the native target.
// It deliberately NEVER calls cmake --build, avoiding re-entrancy into Ninja.
public sealed class PreparedCMakeBuildContext : ICMakeBuildContext
{
    public async Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken ct)
    {
        target = CMakeConfiguration.Resolve(target);
        var artifact = await CMakeFileApi.ResolveExecutableAsync(target, ct);
        if (!File.Exists(artifact.ExecutablePath))
            throw new FileNotFoundException(
                "The prepared native target has no executable. Check the caller's CMake DEPENDS.",
                artifact.ExecutablePath);
        return artifact;
    }
}

// Compatibility adapter: selection is based on observed metadata, never a preset guess.
public static class CMakeConfiguration
{
    public static CMakeTargetRef Resolve(CMakeTargetRef target)
    {
        var observation = CMakeInspection.InspectAsync(target.BuildDirectory, CancellationToken.None)
            .GetAwaiter().GetResult();
        return CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration),
            observation, target.Target);
    }
}

public static class CMakeFileApi
{
    public static async Task<BuiltArtifact> ResolveExecutableAsync(CMakeTargetRef target, CancellationToken ct)
    {
        var observed = await CMakeInspection.InspectAsync(target.BuildDirectory, ct);
        target = CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration), observed, target.Target);
        var item = CMakeInspection.RequireTarget(observed, target, executableOnly: true);
        if (item.Executable is null)
            throw new InvalidOperationException($"Executable artifact for '{target.Target}' is Unknown/ambiguous; no filename is guessed.");
        return new BuiltArtifact(target, item.Executable);
    }
}

public static class ProcessRunner
{
    public static async Task RunAsync(string file, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            // Inherit standard streams: this allows normal IDE/terminal logs and avoids pipe deadlocks.
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        BuildTreeOwnership.PropagateTo(info);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not launch '{file}'.");
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Process '{file}' failed with exit code {process.ExitCode}.");
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            if (args.Length == 3 && args[0] == "context" && args[1] == "inspect")
            {
                var observed = await CMakeInspection.InspectAsync(BuildTreeOwnership.CanonicalPath(args[2]), cts.Token);
                Console.WriteLine(JsonSerializer.Serialize(observed, WorkflowJson.Default.ObservedBuildContext));
                return 0;
            }
            if (args.Length >= 2 && args[0] == "workflow" && args[1] is "plan" or "run")
                return await WorkflowCommand.ExecuteAsync(args[1], args[2..], cts.Token);

            if (args.Length == 3 && args[0] == "workflow" && args[1] == "report")
                return await WorkflowReportCommand.ExecuteAsync(args[2], cts.Token);

            if (args.Length >= 2 && args[0] == "workflow" && args[1] == "sequence")
                return await TwoTargetWorkflow.ExecuteAsync(args[2..], cts.Token);

            if (args.Length < 2 || args[0] != "target" || args[1] is not ("build" or "invoke" or "run" or "resolve"))
            {
                Usage();
                return 2;
            }

            var command = args[1];
            string? buildDir = null, targetName = null, config = null, controller = null;
            var prepared = false;
            var runArgs = new List<string>();
            for (var i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--build-dir" when i + 1 < args.Length: buildDir = args[++i]; break;
                    case "--target" when i + 1 < args.Length: targetName = args[++i]; break;
                    case "--config" when i + 1 < args.Length: config = args[++i]; break;
                    case "--controller-build-dir" when i + 1 < args.Length: controller = args[++i]; break;
                    case "--prepared": prepared = true; break;
                    case "--" when command == "run":
                        runArgs.AddRange(args[(i + 1)..]);
                        i = args.Length;
                        break;
                    default: throw new ArgumentException($"Invalid option: {args[i]}");
                }
            }
            if (string.IsNullOrWhiteSpace(buildDir) || string.IsNullOrWhiteSpace(targetName))
                throw new ArgumentException("--build-dir and --target are required.");
            if (command is "resolve" or "invoke" && prepared)
                throw new ArgumentException("--prepared is only valid with build/run.");

            buildDir = BuildTreeOwnership.CanonicalPath(buildDir);
            if (!prepared && command != "resolve") BuildTreeOwnership.RejectController(buildDir, controller);
            // Prepared calls run under the native caller's DEPENDS/ownership contract.
            // Taking our lock there could deadlock an explicit invoke of that bridge.
            using var ownership = !prepared && command != "resolve"
                ? await BuildTreeOwnership.AcquireAsync([buildDir], cts.Token) : null;
            var target = CMakeConfiguration.Resolve(
                new CMakeTargetRef(buildDir, targetName, config));
            if (command == "invoke" || command == "build" && !prepared)
            {
                var observed = await CMakeInspection.InspectAsync(buildDir, cts.Token);
                if (command == "build") CMakeInspection.RequireTarget(observed, target);
                var invocation = CMakeBuildCommand.Create(target);
                Console.Error.WriteLine(command == "invoke"
                    ? "[CMakeWorkflow] Explicit target invocation; arbitrary native commands may execute."
                    : "[CMakeWorkflow] Native build request; incremental verification belongs to CMake/backend.");
                await ProcessRunner.RunAsync(observed.CMakeExecutable ?? "cmake", invocation.Arguments, invocation.WorkingDirectory, cts.Token);
                return 0;
            }
            ICMakeBuildContext context = prepared
                ? new PreparedCMakeBuildContext()
                : new ExternalCMakeBuildContext();
            var artifact = command == "resolve"
                ? await CMakeFileApi.ResolveExecutableAsync(target, cts.Token)
                : await context.EnsureBuiltAsync(target, cts.Token);
            Console.Error.WriteLine($"[CMakeWorkflow] {target.Target} ({target.Configuration ?? "default"}) => {artifact.ExecutablePath}");

            if (command == "run")
            {
                if (!File.Exists(artifact.ExecutablePath))
                    throw new FileNotFoundException("Built executable is missing.", artifact.ExecutablePath);
                await ProcessRunner.RunAsync(artifact.ExecutablePath, runArgs, target.BuildDirectory, cts.Token);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[CMakeWorkflow] Canceled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] {error.Message}");
            return 1;
        }
    }

    private static void Usage() => Console.Error.WriteLine(
        "Usage: CMakeWorkflow context inspect BUILD_DIR\n" +
        "       CMakeWorkflow target <build|invoke|run|resolve> --build-dir DIR --target NAME [--config CONFIG] [--prepared] [-- executable-args...]\n" +
        "  build: request a known native executable/library target through CMake\n" +
        "  run:   build (or verify prepared target), then execute the binary\n" +
        "  resolve: only inspect the CMake File API codemodel; never build\n" +
        "  invoke: intentionally invoke any target, including utility/campaign targets\n" +
        "  --prepared: only for a CMake custom target with a DEPENDS edge\n" +
        "       CMakeWorkflow workflow sequence --build-dir DIR --first TARGET --second TARGET\n" +
        "         [--config CONFIG] [--first-arg ARG]... [--second-arg ARG]...\n" +
        "         [--controller-build-dir DIR] (only when invoked by a CMake build target)\n" +
        "  workflow sequence: build first, execute first, build second, execute second\n" +
        "       CMakeWorkflow workflow <plan|run> FILE [--only NODE]... [--jobs N]\n" +
        "       CMakeWorkflow workflow report RUN_DIRECTORY\n" +
        "         [--results-dir DIR] [--controller-build-dir DIR]\n" +
        "  plan: validate and show the selected DAG without building or running\n" +
        "  run: execute dependencies, retain logs and write run.json");
}
