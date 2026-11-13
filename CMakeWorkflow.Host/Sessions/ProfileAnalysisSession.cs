using System.Text.Json;
using System.Text.RegularExpressions;
using CMakeWorkflow.Host.Profiling;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Profiling.Analysis;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    private static readonly string[] ProfileTraceSidecarSuffixes =
    [
    ".perf-report.txt",
    ".toc.xml",
    ".manifest.json",
    ".events.xml"
];
    private static readonly string[] ProfileSummarySidecarSuffixes =
    [
    ".json",
    ".stacks.json",
    ".timeline.jsonl",
    ".etw.json",
    ".etw-samples.jsonl"
];
    private static readonly string[] ProfileRootReportNames =
    [
    "analysis.json",
    "comparison.json",
    "counter-comparison.json"
];
    // Saved-trace analysis can be repeated; it is never treated as a durable
    // benchmark run or as new uninstrumented timing samples.
    public Task<ProfileAnalysis> ProfileAnalyzeAsync(ProfileAnalyzeRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, (paths, ct) => AnalyzeProfileAsync(paths, request.RunDirectory, ct),
            cancellationToken);

    internal static async Task<ProfileAnalysis> AnalyzeProfileAsync(WorkspaceReadPaths paths, string runDirectory,
        CancellationToken ct)
    {
        string root = paths.Require(runDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        string[] captures = Directory.GetFiles(root, "*.results.json", SearchOption.TopDirectoryOnly);
        if (captures.Length is < 1 or > 256)
        {
            throw new ApplicationRefusal("invalid_data", "Expected 1..256 capture result files.");
        }
        // ProfileAnalyzer uses capture identifiers for output paths; validate
        // names and linked paths before invoking external analysis tools.
        foreach (string capture in captures)
        {
            paths.Read(capture);
            using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(capture, ct));
            JsonElement item = doc.RootElement;
            string? captureId = ReadCaptureText(item, "captureId");
            string? raw = ReadCaptureText(item, "rawTrace");
            if (captureId is null || !Regex.IsMatch(captureId, "^[a-zA-Z0-9_-]{1,80}$",
                    RegexOptions.CultureInvariant) || string.IsNullOrWhiteSpace(raw))
            {
                throw new ApplicationRefusal("invalid_data", "Invalid profile capture identity.");
            }

            _ = paths.Require(Path.Combine(root, raw));
            foreach (string? suffix in ProfileTraceSidecarSuffixes)
            {
                _ = paths.Require(Path.Combine(root, captureId + suffix));
            }

            foreach (string? suffix in ProfileSummarySidecarSuffixes)
            {
                _ = paths.Require(Path.Combine(root, "traces", "normalized", captureId + suffix));
            }
        }

        foreach (string? artifact in ProfileRootReportNames)
        {
            _ = paths.Require(Path.Combine(root, artifact));
        }

        _ = await ProfileAnalyzer.AnalyzeAsync(root, ct, emitResultPath: false);
        string analysis = paths.Require(Path.Combine(root, "analysis.json"));
        return !File.Exists(analysis)
            ? throw new InvalidDataException("Profiler analysis did not produce analysis.json.")
            : new ProfileAnalysis(1, root, analysis);
    }
    private static string? ReadCaptureText(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

}