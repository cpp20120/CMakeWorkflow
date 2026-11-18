namespace CMakeWorkflow.CMake.Build;

// Compatibility adapter: selection is based on observed metadata, never a preset guess.
public static class CMakeConfiguration
{
    public static async Task<CMakeTargetRef> ResolveAsync(CMakeTargetRef target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObservedBuildContext observation = await CMakeInspection.InspectAsync(target.BuildDirectory, ct);
        return CMakeInspection.Resolve(new RequestedBuildContext(target.BuildDirectory, target.Configuration),
            observation, target.Target);
    }
}
