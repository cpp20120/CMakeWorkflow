using CMakeWorkflow.CMake.Execution;
namespace CMakeWorkflow.CMake.Build;

public sealed class ExternalCMakeBuildContext : ICMakeBuildContext
{

    public async Task<BuiltArtifact> EnsureBuiltAsync(CMakeTargetRef target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObservedBuildContext observed = await CMakeInspection.InspectAsync(target.BuildDirectory, cancellationToken);
        target = CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration),
            observed, target.Target);
        _ = CMakeInspection.RequireTarget(observed, target, executableOnly: true);
        CMakeBuildCommand command = CMakeBuildCommand.Create(target);
        await ProcessRunner.RunAsync(observed.CMakeExecutable ?? "cmake", command.Arguments, command.WorkingDirectory,
            cancellationToken);
        return await CMakeFileApi.ResolveExecutableAsync(target, cancellationToken);
    }
}
