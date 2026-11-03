using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Profiling.Backends;

namespace CMakeWorkflow.Profiling;

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
            ["requiredCpuSet"] = request.CpuSet,
            ["cpuAffinityLauncher"] = request.CpuAffinityLauncher,
            ["startedAt"] = startedAt.ToString("O"),
            ["metricComparability"] =
                "Backend-specific units and attribution; no cross-platform equivalence is asserted."
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
