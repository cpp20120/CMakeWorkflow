using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

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
