using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Profiling.Backends;

namespace CMakeWorkflow.Profiling;

public sealed class ProfileWorkflowExecutor(IProfileProcess? process = null) : IWorkflowOperationExecutor
{
    private readonly IProfileProcess process = process ?? new ProfileProcess();

    public bool Supports(string kind) => kind == "profile";

    public async Task<WorkflowOperationResult> ExecuteAsync(
        WorkflowOperationRequest operation, CancellationToken ct)
    {
        if (!Supports(operation.Kind)) throw new ArgumentException($"Unsupported operation: {operation.Kind}");
        var backendName = operation.Options.GetValueOrDefault("backend", "auto");
        var mode = operation.Options.GetValueOrDefault("mode", "cpu");
        var timeout = TimeSpan.FromSeconds(double.TryParse(
            operation.Options.GetValueOrDefault("captureTimeoutSeconds"), out var seconds) && seconds > 0
            ? seconds
            : 300);
        var backend = SelectBackend(backendName);
        if (!backend.Capabilities.Modes.Contains(mode))
            throw new InvalidOperationException(
                $"Backend {backend.Capabilities.Backend} does not support mode {mode}.");
        if (operation.RequiredProfiler is { } required
            && (required.Backend != backend.Capabilities.Backend || required.Mode != mode
                                                                 || required.EventSource is { } eventSource &&
                                                                 eventSource != backend.Capabilities.EventSource))
            throw new InvalidOperationException("Required profiler capabilities are unavailable on this host.");
        if (operation.CpuSet is not null &&
            (backend.Capabilities.Backend != "perf" || operation.CpuAffinityLauncher is null))
            throw new InvalidOperationException(
                "Required CPU affinity cannot be guaranteed for this profiler backend.");
        var request = new ProfileRequest(operation.Executable, operation.Arguments,
            operation.WorkingDirectory, operation.Environment, operation.OutputDirectory,
            operation.NodeId, mode, timeout, operation.CpuSet, operation.CpuAffinityLauncher);
        var session = new ProfileSession(operation.OutputDirectory, operation.NodeId);
        await session.StartAsync(backend.Capabilities, request, ct);
        try
        {
            var result = await backend.CaptureAsync(request, ct);
            await session.CompleteAsync(result, ct);
            return new WorkflowOperationResult(result.ExitCode,
                result.CaptureStatus, new Dictionary<string, string>
                {
                    ["backend"] = result.Backend,
                    ["rawTrace"] = Path.GetRelativePath(operation.OutputDirectory, result.RawTrace),
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
