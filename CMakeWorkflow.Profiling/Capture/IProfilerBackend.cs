namespace CMakeWorkflow.Profiling.Capture;

public interface IProfilerBackend
{
    ProfileCapabilities Capabilities { get; }
    Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken cancellationToken);
}
