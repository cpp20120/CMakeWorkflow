using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed class ExternalCMakeBuildContext : ICMakeBuildContext
{
    [SuppressMessage("Naming", "CA1725", Justification = "Preserve the existing public parameter name for named-argument callers.")]
    public async Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObservedBuildContext observed = await CMakeInspection.InspectAsync(target.BuildDirectory, ct);
        target = CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration),
            observed, target.Target);
        _ = CMakeInspection.RequireTarget(observed, target, executableOnly: true);
        CMakeBuildCommand command = CMakeBuildCommand.Create(target);
        await ProcessRunner.RunAsync(observed.CMakeExecutable ?? "cmake", command.Arguments, command.WorkingDirectory,
            ct);
        return await CMakeFileApi.ResolveExecutableAsync(target, ct);
    }
}
