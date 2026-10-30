using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

// Snapshot boundary between a native CMake invoke and a durable workflow run.
// Explicit harnessDirectory remains supported; otherwise only CMake's printed
// "Harness result: .../summary.json" marker authorizes snapshot discovery.
internal static class DagFlowHarnessSnapshot
{
    private static string Source(string buildDirectory, string relative)
    {
        var root = BuildTreeOwnership.CanonicalPath(buildDirectory);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rel = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("harnessDirectory escapes the build tree.");
        // Reject directory links below the canonical build root, including junctions on Windows.
        var at = root;
        foreach (var component in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, component);
            if (Directory.Exists(at) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("harnessDirectory must not traverse a directory link.");
        }
        return full;
    }

    public static void RequireFresh(string buildDirectory, string relative)
    {
        var source = Source(buildDirectory, relative);
        // A reused campaign directory cannot establish that the following files belong
        // to this run. Require an empty/fresh destination rather than silently indexing stale data.
        if (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any())
            throw new InvalidOperationException($"Harness directory is not empty: {source}. Configure a unique output directory for every run.");
    }

    public static async Task<Dictionary<string, string>> CaptureAsync(string buildDirectory,
        string relative, string results, string nodeId, CancellationToken ct, int? group = null)
    {
        var source = Source(buildDirectory, relative);
        var files = new[] { "manifest.json", "summary.json", "runs.jsonl" };
        var destination = group is null ? Path.Combine(results, "harness", nodeId)
            : Path.Combine(results, "harness", nodeId, group.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(destination);
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in files)
        {
            ct.ThrowIfCancellationRequested();
            var original = Path.Combine(source, name);
            if (!File.Exists(original) || (File.GetAttributes(original) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Harness output is missing or linked: {original}");
            var before = new FileInfo(original);
            var length = before.Length;
            var modified = before.LastWriteTimeUtc;
            var copy = Path.Combine(destination, name);
            await using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var saved = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(saved, ct);
            before.Refresh();
            if (before.Length != length || before.LastWriteTimeUtc != modified || new FileInfo(copy).Length != length)
                throw new InvalidDataException($"Harness output changed during snapshot: {original}");
            outputs[group is null ? "dagflow-" + name : "dagflow-" + group.Value + "-" + name] = Path.GetRelativePath(results, copy);
        }
        // A producer-side replayCases descriptor makes this harness eligible for
        // per-case native profiling. Preserve the *original* binary now: old
        // build-tree paths may be overwritten by the next benchmark campaign.
        // No descriptor means a legacy timing-only harness, not an error.
        var manifestPath = Path.Combine(destination, "manifest.json");
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct)))
            if (doc.RootElement.TryGetProperty("replayCases", out var cases)
                && cases.ValueKind == JsonValueKind.Object && cases.EnumerateObject().Any(item =>
                    item.Value.ValueKind == JsonValueKind.Object
                    && item.Value.TryGetProperty("replayable", out var enabled)
                    && enabled.ValueKind == JsonValueKind.True))
            {
                var binary = doc.RootElement.GetProperty("binary").GetString()
                    ?? throw new InvalidDataException("Replay manifest has no native binary.");
                var expected = doc.RootElement.GetProperty("binary_sha256").GetString()
                    ?? throw new InvalidDataException("Replay manifest has no binary hash.");
                if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDataException("Replay manifest has an invalid binary hash.");
                if (!Path.IsPathFullyQualified(binary))
                    throw new InvalidDataException("Native benchmark binary must have an absolute path.");
                // Source() checks every directory component, including a symlinked
                // build subdirectory. The leaf itself must also be a regular file.
                var original = Source(buildDirectory, Path.GetRelativePath(
                    BuildTreeOwnership.CanonicalPath(buildDirectory), binary));
                if (!File.Exists(original) || (File.GetAttributes(original) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Replay binary is missing or linked: " + original);
                var saved = Path.Combine(destination, "replay-binary" + (OperatingSystem.IsWindows() ? ".exe" : ""));
                await using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(saved, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Bound disk usage for a single replayable executable.
                    if (input.Length > 512L * 1024 * 1024)
                        throw new InvalidDataException("Replay executable exceeds 512 MiB.");
                    await input.CopyToAsync(output, ct);
                }
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(saved, File.GetUnixFileMode(original));
                await using (var copy = File.OpenRead(saved))
                {
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(copy, ct));
                    if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Replay executable hash differs from manifest; refusing stale/racy binary.");
                }
                outputs[group is null ? "dagflow-replay-binary" : "dagflow-" + group.Value + "-replay-binary"] =
                    Path.GetRelativePath(results, saved);
            }
        return outputs;
    }

    // A DagFlow harness already reports its actual result path after execution.
    // This is deliberately different from scanning build trees or requiring a
    // per-run hard-coded harnessDirectory in the workflow document.
    public static async Task<Dictionary<string, string>> CaptureReportedAsync(
        string buildDirectory, string stdoutLog, string results, string nodeId,
        DateTimeOffset startedAt, CancellationToken ct)
    {
        if (!File.Exists(stdoutLog)) return new(StringComparer.Ordinal);
        const string marker = "Harness result: ";
        var candidates = new HashSet<string>(WorkflowPlanner.PathComparer);
        foreach (var line in File.ReadLines(stdoutLog))
        {
            ct.ThrowIfCancellationRequested();
            // CMake prepends "-- " when emitting message(STATUS ...).
            var trimmed = line.Trim();
            if (trimmed.StartsWith("-- ", StringComparison.Ordinal)) trimmed = trimmed[3..];
            if (!trimmed.StartsWith(marker, StringComparison.Ordinal)) continue;
            var summary = trimmed[marker.Length..].Trim();
            if (!Path.IsPathFullyQualified(summary) || Path.GetFileName(summary) != "summary.json")
                throw new InvalidDataException($"Invalid DagFlow harness result path: {summary}");
            var directory = Path.GetDirectoryName(summary)!;
            var relative = Path.GetRelativePath(BuildTreeOwnership.CanonicalPath(buildDirectory),
                BuildTreeOwnership.CanonicalPath(directory));
            // Source validates containment and refuses junctions/symlinks in the
            // untrusted path. The files themselves are checked by CaptureAsync.
            var safe = Source(buildDirectory, relative);
            foreach (var name in new[] { "manifest.json", "summary.json", "runs.jsonl" })
            {
                var file = Path.Combine(safe, name);
                if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < startedAt.UtcDateTime)
                    throw new InvalidDataException($"DagFlow output was not produced by this invocation: {file}");
            }
            candidates.Add(relative);
        }
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        var ordered = candidates.Order(WorkflowPlanner.PathComparer).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var captured = await CaptureAsync(buildDirectory, ordered[i], results, nodeId, ct,
                ordered.Length == 1 ? null : i);
            foreach (var (key, value) in captured) all.Add(key, value);
        }
        return all;
    }

}
