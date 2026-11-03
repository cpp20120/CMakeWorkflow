using CMakeWorkflow;

namespace CMakeWorkflow.Profiling;

public interface IProfilerBackend
{
    ProfileCapabilities Capabilities { get; }
    Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken cancellationToken);
}
