using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.Profiling;

// Replay never profiles a CMake UTILITY wrapper. Direct run nodes and opted-in
// native harness cases are replayable only with preserved binary identity.
// The replay is instrumented evidence, not another timing sample.
public static class ProfileReplayCommand
{
    private sealed record Input(string SourceDirectory, string NodeId, string Executable,
        string Sha256, string WorkingDirectory, string[] Arguments, string? CpuSet,
        string? WorkloadRecipe);

    private static async Task<Input> LoadAsync(string directory, string nodeId,
        string? caseName, int harnessIndex, CancellationToken ct)
    {
        directory = Path.GetFullPath(directory);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "run.json"), ct));
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "succeeded")
            throw new InvalidDataException("Profile replay requires a succeeded workflow run.");
        var matches = root.GetProperty("nodes").EnumerateArray()
            .Where(x => x.GetProperty("id").GetString() == nodeId).ToArray();
        if (matches.Length != 1 || matches[0].GetProperty("status").GetString() != "succeeded")
            throw new InvalidDataException($"Node '{nodeId}' must have succeeded.");
        var node = matches[0];
        if (caseName is not null)
        {
            if (node.GetProperty("kind").GetString() != "invoke")
                throw new InvalidDataException("--case is valid only for native harness invoke nodes.");
            return await LoadHarnessCaseAsync(directory, node, nodeId, caseName, harnessIndex, ct);
        }
        if (node.GetProperty("kind").GetString() != "run")
            throw new InvalidDataException("A direct run node or an explicit --case is required; CMake wrappers are not profiled.");
        var artifact = node.GetProperty("artifact");
        var executable = artifact.GetProperty("executable").GetString()!;
        var expected = artifact.GetProperty("sha256").GetString()!;
        if (!Path.IsPathFullyQualified(executable) || expected.Length != 64
            || expected.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidDataException("Run report has no verified executable identity.");
        var overrideHash = node.GetProperty("environmentOverrideHash").GetString();
        if (!string.Equals(overrideHash,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cannot replay a run with environment overrides absent from the report.");
        var args = node.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var cwd = node.GetProperty("workingDirectory").GetString()!;
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
        if (!File.Exists(executable)) throw new FileNotFoundException("Old benchmark executable no longer exists.", executable);
        await using var stream = File.OpenRead(executable);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Benchmark executable changed since its original run; preserve an immutable build to profile the baseline.");
        return new Input(directory, nodeId, executable, actual, cwd, args, null, null);
    }

    // The source harness writes a versioned replayCases descriptor containing
    // exact *native* argv/cwd and effective CPU affinity. The C# snapshotter
    // copies its binary while the original workflow still owns the build tree.
    private static async Task<Input> LoadHarnessCaseAsync(string directory,
        JsonElement node, string nodeId, string caseName, int harnessIndex, CancellationToken ct)
    {
        if (harnessIndex < 0 || harnessIndex > 1000)
            throw new ArgumentOutOfRangeException(nameof(harnessIndex));
        var outputs = node.GetProperty("outputs");
        string prefix;
        if (outputs.TryGetProperty("dagflow-manifest.json", out _))
        {
            if (harnessIndex != 0)
                throw new InvalidDataException("A single harness result has index zero.");
            prefix = "dagflow-";
        }
        else prefix = $"dagflow-{harnessIndex}-";
        string Resolve(string field)
        {
            if (!outputs.TryGetProperty(prefix + field, out var value)
                || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Harness snapshot is missing '{prefix + field}'.");
            var relative = value.GetString()!;
            if (Path.IsPathRooted(relative))
                throw new InvalidDataException("Harness snapshot path is absolute.");
            var absolute = Path.GetFullPath(Path.Combine(directory, relative));
            var rel = Path.GetRelativePath(directory, absolute);
            if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(rel) || !File.Exists(absolute))
                throw new InvalidDataException("Harness snapshot escapes its workflow run.");
            var cursor = directory;
            foreach (var part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, part);
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Harness snapshot contains a linked path.");
            }
            return absolute;
        }
        var manifestPath = Resolve("manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
        var root = manifest.RootElement;
        if (!root.TryGetProperty("replayCases", out var cases)
            || cases.ValueKind != JsonValueKind.Object
            || !cases.TryGetProperty(caseName, out var spec))
            throw new InvalidDataException($"Case '{caseName}' has no native replay descriptor.");
        if (!spec.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1)
            throw new InvalidDataException("Unsupported harness replay descriptor version.");
        if (!spec.TryGetProperty("replayable", out var allowed) || allowed.ValueKind != JsonValueKind.True)
            throw new InvalidDataException($"Case '{caseName}' cannot be safely replayed: " +
                (spec.TryGetProperty("reason", out var reason) ? reason.GetString() : "unknown"));
        var binary = Resolve("replay-binary");
        var expected = root.GetProperty("binary_sha256").GetString()!;
        if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Invalid native binary identity.");
        await using (var stream = File.OpenRead(binary))
            if (!expected.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Snapshotted benchmark binary changed since its original run.");
        var cwd = spec.GetProperty("workingDirectory").GetString()!;
        if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
            throw new DirectoryNotFoundException("Original benchmark cwd is not available: " + cwd);
        var argv = spec.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var cpus = spec.TryGetProperty("cpuSet", out var mask) ? mask.GetString() : null;
        if (!string.IsNullOrEmpty(cpus) && (cpus.Any(c => !char.IsAsciiDigit(c) && c is not (',' or '-'))
            || !OperatingSystem.IsLinux()))
            throw new InvalidDataException("Recorded CPU affinity cannot be reproduced on this platform.");
        var recipe = root.GetProperty("cases").GetProperty(caseName).GetRawText();
        return new Input(directory, nodeId, binary, expected, cwd, argv,
            string.IsNullOrWhiteSpace(cpus) ? null : cpus, recipe);
    }

    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct,
        Action<string>? onRunDirectory = null)
    {
        if (args.Length < 4 || args[0] != "replay")
            throw new ArgumentException("profile replay BASELINE_DIR CANDIDATE_DIR --node ID [--case NAME] [--harness INDEX] [--backend auto|perf|etw] [--results-dir DIR] [--timeout-seconds N]");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--node" or "--case" or "--harness" or "--backend" or "--results-dir" or "--timeout-seconds")
                || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException($"Invalid profile replay option: {args[i]}");
        }
        var nodeId = options.GetValueOrDefault("--node")
            ?? throw new ArgumentException("profile replay requires --node ID.");
        var caseName = options.GetValueOrDefault("--case");
        if (!int.TryParse(options.GetValueOrDefault("--harness", "0"), out var harnessIndex)
            || harnessIndex < 0 || harnessIndex > 1000 || (caseName is null && options.ContainsKey("--harness")))
            throw new ArgumentException("--harness requires --case and an index from 0 to 1000.");
        var before = await LoadAsync(args[1], nodeId, caseName, harnessIndex, ct);
        var after = await LoadAsync(args[2], nodeId, caseName, harnessIndex, ct);
        if (!before.Arguments.SequenceEqual(after.Arguments, StringComparer.Ordinal))
            throw new InvalidOperationException("Baseline and candidate argv differ; automatic replay requires the identical workload.");
        if (!string.Equals(before.WorkloadRecipe, after.WorkloadRecipe, StringComparison.Ordinal))
            throw new InvalidOperationException("Baseline and candidate harness case recipes differ.");
        if (!string.Equals(before.CpuSet, after.CpuSet, StringComparison.Ordinal))
            throw new InvalidOperationException("Baseline and candidate CPU affinity differ.");
        if (string.Equals(before.Executable, after.Executable,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Baseline/candidate reference the same mutable executable path; use independently preserved builds.");
        var backend = options.GetValueOrDefault("--backend", "auto");
        var duration = double.TryParse(options.GetValueOrDefault("--timeout-seconds", "300"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var seconds) && seconds is > 0 and <= 86400
            ? seconds : throw new ArgumentException("--timeout-seconds must be in (0, 86400].");
        var destination = Path.GetFullPath(options.GetValueOrDefault("--results-dir", "out/profiles"));
        var output = Path.Combine(destination, "profile-replay-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")
            + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        onRunDirectory?.Invoke(output);
        var metadata = new JsonObject
        {
            ["schemaVersion"] = 1, ["nodeId"] = nodeId, ["case"] = caseName,
            ["harnessIndex"] = harnessIndex, ["cpuSet"] = before.CpuSet,
            ["sourceBaseline"] = before.SourceDirectory, ["sourceCandidate"] = after.SourceDirectory,
            ["baselineExecutableSha256"] = before.Sha256, ["candidateExecutableSha256"] = after.Sha256,
            ["semantics"] = "Independent, instrumented replays of verified original run argv; not additional uninstrumented timing samples."
        };
        await File.WriteAllTextAsync(Path.Combine(output, "replay.json"), metadata.ToJsonString() + "\n", ct);
        var executor = new ProfileWorkflowExecutor();
        var modes = OperatingSystem.IsLinux() ? new[] { "counters", "cpu", "scheduler" }
            : OperatingSystem.IsWindows() ? new[] { "cpu", "scheduler" } : new[] { "cpu" };
        foreach (var mode in modes)
            foreach (var (label, item) in new[] { ("before", before), ("after", after) })
            {
                // Check binary identity again immediately before each pass;
                // externally mutated build trees are never silently accepted.
                await using (var stream = File.OpenRead(item.Executable))
                    if (!string.Equals(item.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)),
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Executable changed during profiling replay.");
                var name = $"{(mode == "counters" ? "counters" : mode == "scheduler" ? "scheduler" : "capture")}_{label}";
                // For native harness cases, preserve explicit taskset affinity.
                // perf runs taskset as a child; taskset execs the verified binary.
                var profiledExecutable = item.CpuSet is not null ? "taskset" : item.Executable;
                IReadOnlyList<string> profiledArgs = item.CpuSet is not null
                    ? ["-c", item.CpuSet, item.Executable, .. item.Arguments]
                    : item.Arguments;
                var result = await executor.ExecuteAsync(new WorkflowOperationRequest(
                    "profile", profiledExecutable, profiledArgs, item.WorkingDirectory,
                    new Dictionary<string, string>(), new Dictionary<string, string>
                    { ["backend"] = backend, ["mode"] = mode,
                        ["captureTimeoutSeconds"] = duration.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    output, name), ct);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Profile replay {name} failed with exit code {result.ExitCode}.");
            }
        await ProfileAnalyzer.AnalyzeAsync(output, ct);
        Console.WriteLine($"Profile replay: {output}");
        return 0;
    }
}
