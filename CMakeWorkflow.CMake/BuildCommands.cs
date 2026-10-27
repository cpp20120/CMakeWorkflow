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

