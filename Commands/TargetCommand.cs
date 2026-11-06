using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Execution;
using CMakeWorkflow.CMake.Serialization;

namespace CMakeWorkflow.Commands;

/// <summary>Runs direct target commands under the existing build-tree or prepared-caller ownership contract.</summary>
internal static class TargetCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] is not ("build" or "invoke" or "run" or "resolve"))
        {
            return CliUsage.Write();
        }

        string command = args[0];
        string? buildDir = null, targetName = null, config = null, controller = null;
        bool prepared = false, json = false;
        List<string> runArgs = [];
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--build-dir" when i + 1 < args.Length: buildDir = args[++i]; break;
                case "--target" when i + 1 < args.Length: targetName = args[++i]; break;
                case "--config" when i + 1 < args.Length: config = args[++i]; break;
                case "--controller-build-dir" when i + 1 < args.Length: controller = args[++i]; break;
                case "--prepared": prepared = true; break;
                case "--json" when command == "resolve": json = true; break;
                case "--" when command == "run":
                    runArgs.AddRange(args[(i + 1)..]);
                    i = args.Length;
                    break;
                default: throw new ArgumentException($"Invalid option: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(buildDir) || string.IsNullOrWhiteSpace(targetName))
        {
            throw new ArgumentException("--build-dir and --target are required.");
        }

        if (command is "resolve" or "invoke" && prepared)
        {
            throw new ArgumentException("--prepared is only valid with build/run.");
        }

        buildDir = BuildTreeOwnership.CanonicalPath(buildDir);
        if (!prepared && command != "resolve")
        {
            BuildTreeOwnership.RejectController(buildDir, controller);
        }
        // Prepared calls run under the native caller's DEPENDS/ownership contract.
        // Taking our lock there could deadlock an explicit invoke of that bridge.
        using BuildTreeOwnership? ownership = !prepared && command != "resolve"
            ? await BuildTreeOwnership.AcquireAsync([buildDir], ct)
            : null;
        CMakeTargetRef target = CMakeConfiguration.Resolve(
            new CMakeTargetRef(buildDir, targetName, config));
        if (command == "invoke" || (command == "build" && !prepared))
        {
            ObservedBuildContext observed = await CMakeInspection.InspectAsync(buildDir, ct);
            if (command == "build")
            {
                _ = CMakeInspection.RequireTarget(observed, target);
            }

            CMakeBuildCommand invocation = CMakeBuildCommand.Create(target);
            await Console.Error.WriteLineAsync(command == "invoke"
                ? "[CMakeWorkflow] Explicit target invocation; arbitrary native commands may execute."
                : "[CMakeWorkflow] Native build request; incremental verification belongs to CMake/backend.");
            await ProcessRunner.RunAsync(observed.CMakeExecutable ?? "cmake", invocation.Arguments,
                invocation.WorkingDirectory, ct);
            return 0;
        }

        ICMakeBuildContext context = prepared
            ? new PreparedCMakeBuildContext()
            : new ExternalCMakeBuildContext();
        BuiltArtifact artifact = command == "resolve"
            ? await CMakeFileApi.ResolveExecutableAsync(target, ct)
            : await context.EnsureBuiltAsync(target, ct);
        await Console.Error.WriteLineAsync(
            $"[CMakeWorkflow] {target.Target} ({target.Configuration ?? "default"}) => {artifact.ExecutablePath}");
        if (json)
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new TargetResolutionView(1,
                    target.BuildDirectory, target.Target, target.Configuration, artifact.ExecutablePath),
                WorkflowProtocolJson.Default.TargetResolutionView));
        }

        if (command == "run")
        {
            if (!File.Exists(artifact.ExecutablePath))
            {
                throw new FileNotFoundException("Built executable is missing.", artifact.ExecutablePath);
            }

            await ProcessRunner.RunAsync(artifact.ExecutablePath, runArgs, target.BuildDirectory, ct);
        }

        return 0;
    }
}
