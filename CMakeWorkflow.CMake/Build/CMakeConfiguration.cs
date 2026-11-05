namespace CMakeWorkflow.CMake.Build;

// Compatibility adapter: selection is based on observed metadata, never a preset guess.
public static class CMakeConfiguration
{
    public static CMakeTargetRef Resolve(CMakeTargetRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObservedBuildContext observation = CMakeInspection.InspectAsync(target.BuildDirectory, CancellationToken.None)
            .GetAwaiter().GetResult();
        return CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration),
            observation, target.Target);
    }
}
