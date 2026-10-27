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
            ? seconds : 300);
        var backend = SelectBackend(backendName);
        if (!backend.Capabilities.Modes.Contains(mode))
            throw new InvalidOperationException($"Backend {backend.Capabilities.Backend} does not support mode {mode}.");
        var request = new ProfileRequest(operation.Executable, operation.Arguments,
            operation.WorkingDirectory, operation.Environment, operation.OutputDirectory,
            operation.NodeId, mode, timeout);
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

public sealed class ProfileSession(string outputDirectory, string captureId)
{
    private readonly string outputDirectory = outputDirectory;
    private readonly string captureId = captureId;
    private DateTimeOffset startedAt;

    public async Task StartAsync(ProfileCapabilities capabilities, ProfileRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(Path.Combine(outputDirectory, "traces", "normalized"));
        startedAt = DateTimeOffset.UtcNow;
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1, ["captureId"] = captureId,
            ["backend"] = capabilities.Backend, ["mode"] = request.Mode,
            ["eventSource"] = capabilities.EventSource,
            ["launchModel"] = capabilities.LaunchModel,
            ["startGuarantee"] = capabilities.StartGuarantee,
            ["attribution"] = capabilities.Attribution,
            ["executable"] = request.Executable,
            ["executableSha256"] = await CommandExecution.Sha256Async(request.Executable, ct),
            ["startedAt"] = startedAt.ToString("O"),
            ["metricComparability"] = "Backend-specific units and attribution; no cross-platform equivalence is asserted."
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, captureId + ".manifest.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
    }

    public async Task CompleteAsync(ProfileResult result, CancellationToken ct)
    {
        var rawRelative = Path.GetRelativePath(outputDirectory, result.RawTrace);
        var rawHash = await ProfileArtifactHash.Sha256Async(result.RawTrace, ct);
        var record = new JsonObject
        {
            ["captureId"] = captureId, ["backend"] = result.Backend,
            ["mode"] = result.Mode, ["status"] = result.CaptureStatus,
            ["exitCode"] = result.ExitCode,
            ["startedAt"] = result.StartedAt.ToString("O"),
            ["finishedAt"] = result.FinishedAt.ToString("O"),
            ["rawTrace"] = rawRelative,
            ["rawTraceSha256"] = rawHash,
            ["targetProcessId"] = result.TargetProcessId,
            ["targetStartedAt"] = result.TargetStartedAt?.ToString("O"),
            ["targetFinishedAt"] = result.TargetFinishedAt?.ToString("O"),
            ["eventSource"] = result.Capabilities.EventSource,
            ["attribution"] = result.Capabilities.Attribution
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, captureId + ".results.json"),
            record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
        await File.AppendAllTextAsync(Path.Combine(outputDirectory, "runs.jsonl"),
            record.ToJsonString() + "\n", ct);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, captureId + ".profiler.log"), result.Output, ct);
        var normalized = new JsonObject
        {
            ["rawTrace"] = rawRelative, ["backend"] = result.Backend,
            ["rawTraceSha256"] = rawHash,
            ["eventSource"] = result.Capabilities.EventSource,
            ["normalization"] = "metadata-only",
            ["samples"] = null, ["threads"] = null, ["callStacks"] = null
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "traces", "normalized", captureId + ".json"),
            normalized.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
    }

    public async Task FailAsync(Exception error, CancellationToken ct)
    {
        var record = new JsonObject
        {
            ["captureId"] = captureId, ["status"] = error is OperationCanceledException ? "canceled" : "failed",
            ["startedAt"] = startedAt.ToString("O"),
            ["finishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["error"] = error.Message
        };
        await File.AppendAllTextAsync(Path.Combine(outputDirectory, "runs.jsonl"),
            record.ToJsonString() + "\n", ct);
    }
}

public static class ProfileArtifactHash
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        if (File.Exists(path)) return await CommandExecution.Sha256Async(path, ct);
        if (!Directory.Exists(path)) throw new FileNotFoundException("Profile artifact is missing.", path);
        using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var fileHash = await CommandExecution.Sha256Async(file, ct);
            tree.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + fileHash + "\n"));
        }
        return Convert.ToHexString(tree.GetHashAndReset()).ToLowerInvariant();
    }
}
