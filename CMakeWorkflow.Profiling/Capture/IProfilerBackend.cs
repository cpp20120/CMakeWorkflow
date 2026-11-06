// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Capture;
#pragma warning restore IDE0130

public interface IProfilerBackend
{
    ProfileCapabilities Capabilities { get; }
    Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken cancellationToken);
}
