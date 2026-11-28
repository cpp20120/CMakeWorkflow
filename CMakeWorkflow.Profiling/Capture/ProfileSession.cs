using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow.Core;

namespace CMakeWorkflow.Profiling.Capture;

public sealed class ProfileSession(string outputDirectory, string captureId)
{
    private readonly string outputDirectory = outputDirectory;
    private readonly string captureId = captureId;
    private DateTimeOffset startedAt;
    private string? executableSha256;

    public async Task StartAsync(ProfileCapabilities capabilities, ProfileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(request);
        _ = Directory.CreateDirectory(outputDirectory);
        _ = Directory.CreateDirectory(Path.Combine(outputDirectory, "traces", "normalized"));
        startedAt = DateTimeOffset.UtcNow;
        executableSha256 = await CommandExecution.Sha256Async(request.Executable, ct);
        JsonObject manifest = new()
        {
            ["schemaVersion"] = 1,
            ["captureId"] = captureId,
            ["backend"] = capabilities.Backend,
            ["mode"] = request.Mode,
            ["memoryStallEvent"] = request.MemoryStallEvent,
            ["pmcEvents"] = request.PmcEvents,
            ["pmcInterval"] = request.PmcInterval,
            ["eventSource"] = capabilities.EventSource,
            ["launchModel"] = capabilities.LaunchModel,
            ["startGuarantee"] = capabilities.StartGuarantee,
            ["attribution"] = capabilities.Attribution,
            ["executable"] = request.Executable,
            ["executableSha256"] = executableSha256,
            ["requiredCpuSet"] = request.CpuSet,
            ["cpuAffinityLauncher"] = request.CpuAffinityLauncher,
            ["startedAt"] = startedAt.ToString("O"),
            ["metricComparability"] =
                "Backend-specific units and attribution; no cross-platform equivalence is asserted."
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, captureId + ".manifest.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
    }

    public async Task CompleteAsync(ProfileResult result, CancellationToken ct) =>
        _ = await CompleteCoreAsync(result, ct);

    internal async Task<WorkflowProfileEvidence> CompleteWithEvidenceAsync(ProfileResult result, ProfileRequest request,
        CancellationToken ct)
    {
        string executableDigest = executableSha256 ?? throw new InvalidOperationException("Profiler session did not start.");
        CaptureFiles files = await CompleteCoreAsync(result, ct);
        ProfileCapabilities capabilities = result.Capabilities;
        return new(captureId, result.Backend, result.Mode, result.CaptureStatus, result.ExitCode,
            result.StartedAt, result.FinishedAt, request.Executable, executableDigest,
            files.RawTrace, files.RawTraceSha256, files.CounterConfiguration, files.CounterConfigurationSha256,
            result.TargetProcessId, result.TargetStartedAt, result.TargetFinishedAt,
            capabilities.EventSource, capabilities.LaunchModel, capabilities.StartGuarantee, capabilities.Attribution,
            capabilities.Modes, capabilities.RawFormats, request.CpuSet, request.CpuAffinityLauncher,
            request.MemoryStallEvent, request.PmcEvents, request.PmcInterval, request.Timeout.Ticks);
    }

    private sealed record CaptureFiles(string RawTrace, string RawTraceSha256, string? CounterConfiguration,
        string? CounterConfigurationSha256);

    private async Task<CaptureFiles> CompleteCoreAsync(ProfileResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        string rawRelative = Path.GetRelativePath(outputDirectory, result.RawTrace);
        string rawHash = await ProfileArtifactHash.Sha256Async(result.RawTrace, ct);
        string? counterFile = result.CounterConfiguration is { } configuration
            ? Path.GetRelativePath(outputDirectory, configuration) : null;
        string? counterHash = result.CounterConfiguration is { } configurationPath
            ? await ProfileArtifactHash.Sha256Async(configurationPath, ct) : null;
        JsonObject record = new()
        {
            ["captureId"] = captureId,
            ["backend"] = result.Backend,
            ["mode"] = result.Mode,
            ["status"] = result.CaptureStatus,
            ["exitCode"] = result.ExitCode,
            ["startedAt"] = result.StartedAt.ToString("O"),
            ["finishedAt"] = result.FinishedAt.ToString("O"),
            ["rawTrace"] = rawRelative,
            ["rawTraceSha256"] = rawHash,
            ["counterConfiguration"] = counterFile,
            ["counterConfigurationSha256"] = counterHash,
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
        JsonObject normalized = new()
        {
            ["rawTrace"] = rawRelative,
            ["backend"] = result.Backend,
            ["rawTraceSha256"] = rawHash,
            ["eventSource"] = result.Capabilities.EventSource,
            ["normalization"] = "metadata-only",
            ["samples"] = null,
            ["threads"] = null,
            ["callStacks"] = null
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "traces", "normalized", captureId + ".json"),
            normalized.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
        return new(rawRelative, rawHash, counterFile, counterHash);
    }

    public async Task FailAsync(Exception error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(error);
        JsonObject record = new()
        {
            ["captureId"] = captureId,
            ["status"] = error is OperationCanceledException ? "canceled" : "failed",
            ["startedAt"] = startedAt.ToString("O"),
            ["finishedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["error"] = error.Message
        };
        await File.AppendAllTextAsync(Path.Combine(outputDirectory, "runs.jsonl"),
            record.ToJsonString() + "\n", ct);
    }
}
