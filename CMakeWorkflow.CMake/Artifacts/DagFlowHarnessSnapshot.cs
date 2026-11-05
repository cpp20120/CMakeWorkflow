using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;

namespace CMakeWorkflow.CMake.Artifacts;

// Snapshot boundary between a native CMake invoke and a durable workflow run.
// Explicit harnessDirectory remains supported; otherwise only CMake's printed
// "Harness result: .../summary.json" marker authorizes snapshot discovery.
internal static class DagFlowHarnessSnapshot
{
    private static string Source(string buildDirectory, string relative)
    {
        string root = BuildTreeOwnership.CanonicalPath(buildDirectory);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string rel = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(rel) || rel == ".." ||
            rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("harnessDirectory escapes the build tree.");
        }
        // Reject directory links below the canonical build root, including junctions on Windows.
        string at = root;
        foreach (string component in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, component);
            if (Directory.Exists(at) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("harnessDirectory must not traverse a directory link.");
            }
        }

        return full;
    }

    public static void RequireFresh(string buildDirectory, string relative)
    {
        string source = Source(buildDirectory, relative);
        // A reused campaign directory cannot establish that the following files belong
        // to this run. Require an empty/fresh destination rather than silently indexing stale data.
        if (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any())
        {
            throw new InvalidOperationException(
                $"Harness directory is not empty: {source}. Configure a unique output directory for every run.");
        }
    }

    public static async Task<Dictionary<string, string>> CaptureAsync(string buildDirectory,
        string relative, string results, string nodeId, CancellationToken ct, int? group = null)
    {
        string source = Source(buildDirectory, relative);
        string[] files = ["manifest.json", "summary.json", "runs.jsonl"];
        string destination = group is null
            ? Path.Combine(results, "harness", nodeId)
            : Path.Combine(results, "harness", nodeId,
                group.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(destination);
        Dictionary<string, string> outputs = new(StringComparer.Ordinal);
        foreach (string? name in files)
        {
            ct.ThrowIfCancellationRequested();
            string original = Path.Combine(source, name);
            if (!File.Exists(original) || (File.GetAttributes(original) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Harness output is missing or linked: {original}");
            }

            FileInfo before = new(original);
            long length = before.Length;
            DateTime modified = before.LastWriteTimeUtc;
            string copy = Path.Combine(destination, name);
            await using (FileStream input = new(original, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (FileStream saved = new(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(saved, ct);
            }

            before.Refresh();
            if (before.Length != length || before.LastWriteTimeUtc != modified || new FileInfo(copy).Length != length)
            {
                throw new InvalidDataException($"Harness output changed during snapshot: {original}");
            }

            outputs[group is null ? "dagflow-" + name : "dagflow-" + group.Value + "-" + name] =
                Path.GetRelativePath(results, copy);
        }

        // A producer-side replayCases descriptor makes this harness eligible for
        // per-case native profiling. Preserve the *original* binary now: old
        // build-tree paths may be overwritten by the next benchmark campaign.
        // No descriptor means a legacy timing-only harness, not an error.
        string manifestPath = Path.Combine(destination, "manifest.json");
        using (JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct)))
        {
            if (doc.RootElement.TryGetProperty("replayCases", out JsonElement cases)
                && cases.ValueKind == JsonValueKind.Object && cases.EnumerateObject().Any(item =>
                    item.Value.ValueKind == JsonValueKind.Object
                    && item.Value.TryGetProperty("replayable", out JsonElement enabled)
                    && enabled.ValueKind == JsonValueKind.True))
            {
                string binary = doc.RootElement.GetProperty("binary").GetString()
                             ?? throw new InvalidDataException("Replay manifest has no native binary.");
                string expected = doc.RootElement.GetProperty("binary_sha256").GetString()
                               ?? throw new InvalidDataException("Replay manifest has no binary hash.");
                if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
                {
                    throw new InvalidDataException("Replay manifest has an invalid binary hash.");
                }

                if (!Path.IsPathFullyQualified(binary))
                {
                    throw new InvalidDataException("Native benchmark binary must have an absolute path.");
                }
                // Source() checks every directory component, including a symlinked
                // build subdirectory. The leaf itself must also be a regular file.
                string original = Source(buildDirectory, Path.GetRelativePath(
                    BuildTreeOwnership.CanonicalPath(buildDirectory), binary));
                if (!File.Exists(original) || (File.GetAttributes(original) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Replay binary is missing or linked: " + original);
                }

                string saved = Path.Combine(destination, "replay-binary" + (OperatingSystem.IsWindows() ? ".exe" : ""));
                await using (FileStream input = new(original, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (FileStream output = new(saved, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Bound disk usage for a single replayable executable.
                    if (input.Length > 512L * 1024 * 1024)
                    {
                        throw new InvalidDataException("Replay executable exceeds 512 MiB.");
                    }

                    await input.CopyToAsync(output, ct);
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(saved, File.GetUnixFileMode(original));
                }

                await using (FileStream copy = File.OpenRead(saved))
                {
                    string hash = Convert.ToHexString(await SHA256.HashDataAsync(copy, ct));
                    if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Replay executable hash differs from manifest; refusing stale/racy binary.");
                    }
                }

                outputs[group is null ? "dagflow-replay-binary" : "dagflow-" + group.Value + "-replay-binary"] =
                    Path.GetRelativePath(results, saved);
            }
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
        if (!File.Exists(stdoutLog))
        {
            return new(StringComparer.Ordinal);
        }

        const string marker = "Harness result: ";
        HashSet<string> candidates = new(WorkflowPlanner.PathComparer);
        foreach (string line in await File.ReadAllLinesAsync(stdoutLog, ct))
        {
            ct.ThrowIfCancellationRequested();
            // CMake prepends "-- " when emitting message(STATUS ...).
            string trimmed = line.Trim();
            if (trimmed.StartsWith("-- ", StringComparison.Ordinal))
            {
                trimmed = trimmed[3..];
            }

            if (!trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            string summary = trimmed[marker.Length..].Trim();
            if (!Path.IsPathFullyQualified(summary) || Path.GetFileName(summary) != "summary.json")
            {
                throw new InvalidDataException($"Invalid DagFlow harness result path: {summary}");
            }

            string directory = Path.GetDirectoryName(summary)!;
            string relative = Path.GetRelativePath(BuildTreeOwnership.CanonicalPath(buildDirectory),
                BuildTreeOwnership.CanonicalPath(directory));
            // Source validates containment and refuses junctions/symlinks in the
            // untrusted path. The files themselves are checked by CaptureAsync.
            string safe = Source(buildDirectory, relative);
            foreach (string? name in new[] { "manifest.json", "summary.json", "runs.jsonl" })
            {
                string file = Path.Combine(safe, name);
                if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < startedAt.UtcDateTime)
                {
                    throw new InvalidDataException($"DagFlow output was not produced by this invocation: {file}");
                }
            }

            _ = candidates.Add(relative);
        }

        Dictionary<string, string> all = new(StringComparer.Ordinal);
        string[] ordered = [.. candidates.Order(WorkflowPlanner.PathComparer)];
        for (int i = 0; i < ordered.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            Dictionary<string, string> captured = await CaptureAsync(buildDirectory, ordered[i], results, nodeId, ct,
                ordered.Length == 1 ? null : i);
            foreach ((string? key, string? value) in captured)
            {
                all.Add(key, value);
            }
        }

        return all;
    }
}