using CMakeWorkflow.Core;
using CMakeWorkflow.Profiling.Backends;
using CMakeWorkflow.Profiling.Capture;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Workflows;
#pragma warning restore IDE0130

public sealed class ProfileWorkflowExecutor(IProfileProcess? process = null) : IWorkflowOperationExecutor
{
    private readonly IProfileProcess process = process ?? new ProfileProcess();

    public bool Supports(string kind) => kind == "profile";

    public async Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Supports(request.Kind))
        {
            throw new ArgumentException($"Unsupported operation: {request.Kind}");
        }

        string backendName = request.Options.GetValueOrDefault("backend", "auto");
        string mode = request.Options.GetValueOrDefault("mode", "cpu");
        TimeSpan timeout = TimeSpan.FromSeconds(double.TryParse(
            request.Options.GetValueOrDefault("captureTimeoutSeconds"), out double seconds) && seconds > 0
            ? seconds
            : 300);
        IProfilerBackend backend = SelectBackend(backendName);
        if (!backend.Capabilities.Modes.Contains(mode))
        {
            throw new InvalidOperationException(
                $"Backend {backend.Capabilities.Backend} does not support mode {mode}.");
        }

        if (request.RequiredProfiler is { } required
            && (required.Backend != backend.Capabilities.Backend || required.Mode != mode
                                                                 || (required.EventSource is { } eventSource &&
                                                                 eventSource != backend.Capabilities.EventSource)))
        {
            throw new InvalidOperationException("Required profiler capabilities are unavailable on this host.");
        }

        if (request.CpuSet is not null &&
            (backend.Capabilities.Backend != "perf" || request.CpuAffinityLauncher is null))
        {
            throw new InvalidOperationException(
                "Required CPU affinity cannot be guaranteed for this profiler backend.");
        }

        ProfileRequest captureRequest = new(request.Executable, request.Arguments,
            request.WorkingDirectory, request.Environment, request.OutputDirectory,
            request.NodeId, mode, timeout, request.CpuSet, request.CpuAffinityLauncher);
        ProfileSession session = new(request.OutputDirectory, request.NodeId);
        await session.StartAsync(backend.Capabilities, captureRequest, cancellationToken);
        try
        {
            ProfileResult result = await backend.CaptureAsync(captureRequest, cancellationToken);
            await session.CompleteAsync(result, cancellationToken);
            return new WorkflowOperationResult(result.ExitCode,
                result.CaptureStatus, new Dictionary<string, string>
                {
                    ["backend"] = result.Backend,
                    ["rawTrace"] = Path.GetRelativePath(request.OutputDirectory, result.RawTrace),
                    ["eventSource"] = result.Capabilities.EventSource
                });
        }
        catch (Exception error)
        {
            await session.FailAsync(error, CancellationToken.None);
            throw;
        }
    }

    private IProfilerBackend SelectBackend(string name) => name switch
    {
        "auto" when OperatingSystem.IsLinux() => new LinuxPerfBackend(process),
        "auto" when OperatingSystem.IsWindows() => new WindowsEtwBackend(process),
        "auto" when OperatingSystem.IsMacOS() => new MacInstrumentsBackend(process),
        "perf" when OperatingSystem.IsLinux() => new LinuxPerfBackend(process),
        "etw" when OperatingSystem.IsWindows() => new WindowsEtwBackend(process),
        "instruments" when OperatingSystem.IsMacOS() => new MacInstrumentsBackend(process),
        _ => throw new PlatformNotSupportedException($"Profiling backend '{name}' is not available on this host.")
    };
}
