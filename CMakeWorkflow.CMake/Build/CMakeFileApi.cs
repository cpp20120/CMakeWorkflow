namespace CMakeWorkflow.CMake.Build;

public static class CMakeFileApi
{
    public static async Task<BuiltArtifact> ResolveExecutableAsync(CMakeTargetRef target, CancellationToken ct,
        Action<string>? readGuard = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObservedBuildContext observed = await CMakeInspection.InspectAsync(target.BuildDirectory, ct, readGuard);
        target = CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration),
            observed, target.Target);
        NativeTargetObservation item = CMakeInspection.RequireTarget(observed, target, executableOnly: true);
        return item.Executable is null
            ? throw new InvalidOperationException(
                $"Executable artifact for '{target.Target}' is Unknown/ambiguous; no filename is guessed.")
            : new BuiltArtifact(target, item.Executable);
    }
}
