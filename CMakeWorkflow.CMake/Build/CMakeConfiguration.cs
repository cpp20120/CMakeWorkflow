#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

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
