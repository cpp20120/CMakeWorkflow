using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow.Core;
using CMakeWorkflow.Profiling.Analysis;
using CMakeWorkflow.Profiling.Workflows;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Replay;
#pragma warning restore IDE0130

// Replay never profiles a CMake UTILITY wrapper. Direct run nodes and opted-in
// native harness cases are replayable only with preserved binary identity.
// The replay is instrumented evidence, not another timing sample.
public static class ProfileReplayCommand
{
    private sealed record Input(
        string SourceDirectory,
        string NodeId,
        string Executable,
        string Sha256,
        string WorkingDirectory,
        string[] Arguments,
        string? CpuSet,
        string? WorkloadRecipe);

    private static async Task<Input> LoadAsync(string directory, string nodeId,
        string? caseName, int harnessIndex, CancellationToken ct, Action<string>? pathGuard = null)
    {
        directory = Path.GetFullPath(directory);
        pathGuard?.Invoke(Path.Combine(directory, "run.json"));
        using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "run.json"), ct));
        JsonElement root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "succeeded")
        {
            throw new InvalidDataException("Profile replay requires a succeeded workflow run.");
        }

        JsonElement[] matches = [.. root.GetProperty("nodes").EnumerateArray().Where(x => x.GetProperty("id").GetString() == nodeId)];
        if (matches.Length != 1 || matches[0].GetProperty("status").GetString() != "succeeded")
        {
            throw new InvalidDataException($"Node '{nodeId}' must have succeeded.");
        }

        JsonElement node = matches[0];
        if (caseName is not null)
        {
            return node.GetProperty("kind").GetString() != "invoke"
                ? throw new InvalidDataException("--case is valid only for native harness invoke nodes.")
                : await LoadHarnessCaseAsync(directory, node, nodeId, caseName, harnessIndex, ct, pathGuard);
        }

        if (node.GetProperty("kind").GetString() != "run")
        {
            throw new InvalidDataException(
                "A direct run node or an explicit --case is required; CMake wrappers are not profiled.");
        }

        JsonElement artifact = node.GetProperty("artifact");
        string executable = artifact.GetProperty("executable").GetString()!;
        string expected = artifact.GetProperty("sha256").GetString()!;
        if (!Path.IsPathFullyQualified(executable) || expected.Length != 64
                                                   || expected.Any(x => !Uri.IsHexDigit(x)))
        {
            throw new InvalidDataException("Run report has no verified executable identity.");
        }

        string? overrideHash = node.GetProperty("environmentOverrideHash").GetString();
        if (!string.Equals(overrideHash,
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Cannot replay a run with environment overrides absent from the report.");
        }

        string[] args = [.. node.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!)];
        string cwd = node.GetProperty("workingDirectory").GetString()!;
        pathGuard?.Invoke(executable);
        pathGuard?.Invoke(cwd);
        if (!Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException(cwd);
        }

        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Old benchmark executable no longer exists.", executable);
        }

        await using FileStream stream = File.OpenRead(executable);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidOperationException(
                "Benchmark executable changed since its original run; preserve an immutable build to profile the baseline.")
            : new Input(directory, nodeId, executable, actual, cwd, args, null, null);
    }

    // The source harness writes a versioned replayCases descriptor containing
    // exact *native* argv/cwd and effective CPU affinity. The C# snapshotter
    // copies its binary while the original workflow still owns the build tree.
    private static async Task<Input> LoadHarnessCaseAsync(string directory,
        JsonElement node, string nodeId, string caseName, int harnessIndex, CancellationToken ct,
        Action<string>? pathGuard = null)
    {
        if (harnessIndex is < 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(harnessIndex));
        }

        JsonElement outputs = node.GetProperty("outputs");
        string prefix;
        if (outputs.TryGetProperty("dagflow-manifest.json", out _))
        {
            if (harnessIndex != 0)
            {
                throw new InvalidDataException("A single harness result has index zero.");
            }

            prefix = "dagflow-";
        }
        else
        {
            prefix = $"dagflow-{harnessIndex}-";
        }

        string Resolve(string field)
        {
            if (!outputs.TryGetProperty(prefix + field, out JsonElement value)
                || value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Harness snapshot is missing '{prefix + field}'.");
            }

            string relative = value.GetString()!;
            if (Path.IsPathRooted(relative))
            {
                throw new InvalidDataException("Harness snapshot path is absolute.");
            }

            string absolute = Path.GetFullPath(Path.Combine(directory, relative));
            pathGuard?.Invoke(absolute);
            string rel = Path.GetRelativePath(directory, absolute);
            if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                            || Path.IsPathRooted(rel) || !File.Exists(absolute))
            {
                throw new InvalidDataException("Harness snapshot escapes its workflow run.");
            }

            string cursor = directory;
            foreach (string part in rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                cursor = Path.Combine(cursor, part);
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Harness snapshot contains a linked path.");
                }
            }

            return absolute;
        }

        string manifestPath = Resolve("manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
        JsonElement root = manifest.RootElement;
        if (!root.TryGetProperty("replayCases", out JsonElement cases)
            || cases.ValueKind != JsonValueKind.Object
            || !cases.TryGetProperty(caseName, out JsonElement spec))
        {
            throw new InvalidDataException($"Case '{caseName}' has no native replay descriptor.");
        }

        if (!spec.TryGetProperty("schemaVersion", out JsonElement version) || version.GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported harness replay descriptor version.");
        }

        if (!spec.TryGetProperty("replayable", out JsonElement allowed) || allowed.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException($"Case '{caseName}' cannot be safely replayed: " +
                                           (spec.TryGetProperty("reason", out JsonElement reason)
                                               ? reason.GetString()
                                               : "unknown"));
        }

        string binary = Resolve("replay-binary");
        string expected = root.GetProperty("binary_sha256").GetString()!;
        if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidDataException("Invalid native binary identity.");
        }

        await using (FileStream stream = File.OpenRead(binary))
        {
            if (!expected.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Snapshotted benchmark binary changed since its original run.");
            }
        }

        string cwd = spec.GetProperty("workingDirectory").GetString()!;
        pathGuard?.Invoke(cwd);
        if (!Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
        {
            throw new DirectoryNotFoundException("Original benchmark cwd is not available: " + cwd);
        }

        string[] argv = [.. spec.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()!)];
        string? cpus = spec.TryGetProperty("cpuSet", out JsonElement mask) ? mask.GetString() : null;
        if (!string.IsNullOrEmpty(cpus) && (cpus.Any(c => !char.IsAsciiDigit(c) && c is not (',' or '-'))
                                            || !OperatingSystem.IsLinux()))
        {
            throw new InvalidDataException("Recorded CPU affinity cannot be reproduced on this platform.");
        }

        string recipe = root.GetProperty("cases").GetProperty(caseName).GetRawText();
        return new Input(directory, nodeId, binary, expected, cwd, argv,
            string.IsNullOrWhiteSpace(cpus) ? null : cpus, recipe);
    }

    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct,
        Action<string>? onRunDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length < 4 || args[0] != "replay")
        {
            throw new ArgumentException(
                "profile replay BASELINE_DIR CANDIDATE_DIR --node ID [--case NAME] [--harness INDEX] [--backend auto|perf|etw] [--results-dir DIR] [--timeout-seconds N]");
        }

        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int i = 3; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--node" or "--case" or "--harness" or "--backend"
                                         or "--results-dir" or "--timeout-seconds")
                                     || !options.TryAdd(args[i], args[i + 1]))
            {
                throw new ArgumentException($"Invalid profile replay option: {args[i]}");
            }
        }

        string nodeId = options.GetValueOrDefault("--node")
                     ?? throw new ArgumentException("profile replay requires --node ID.");
        string? caseName = options.GetValueOrDefault("--case");
        if (!int.TryParse(options.GetValueOrDefault("--harness", "0"), out int harnessIndex)
            || harnessIndex < 0 || harnessIndex > 1000 || (caseName is null && options.ContainsKey("--harness")))
        {
            throw new ArgumentException("--harness requires --case and an index from 0 to 1000.");
        }

        ProfileReplayOutcome result = await ReplayAsync(new ProfileReplayOptions(args[1], args[2], nodeId,
            caseName, harnessIndex, options.GetValueOrDefault("--backend", "auto"),
            options.GetValueOrDefault("--results-dir", "out/profiles"),
            double.TryParse(options.GetValueOrDefault("--timeout-seconds", "300"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out double parsedSeconds)
                ? parsedSeconds
                : double.NaN), ct, onRunDirectory);
        Console.WriteLine($"Profile replay: {result.RunDirectory}");
        return 0;
    }

    // Typed application entry: validation/replay are shared by the CLI adapter
    // and Host RPC. The optional guard restricts all executable evidence paths.
    public static async Task<ProfileReplayOutcome> ReplayAsync(ProfileReplayOptions request,
        CancellationToken ct, Action<string>? onRunDirectory = null, Action<string>? pathGuard = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.NodeId) || request.HarnessIndex is < 0 or > 1000
                                                      || (request.CaseName is null && request.HarnessIndex != 0))
        {
            throw new ArgumentException("Invalid profile replay node, case or harness index.");
        }

        if (!double.IsFinite(request.TimeoutSeconds) || request.TimeoutSeconds is <= 0 or > 86400)
        {
            throw new ArgumentException("--timeout-seconds must be in (0, 86400].");
        }

        if (request.Backend is not ("auto" or "perf" or "etw" or "instruments"))
        {
            throw new ArgumentException("Invalid profiling backend.");
        }

        pathGuard?.Invoke(request.BaselineDirectory);
        pathGuard?.Invoke(request.CandidateDirectory);
        pathGuard?.Invoke(request.ResultsDirectory);
        Input before = await LoadAsync(request.BaselineDirectory, request.NodeId,
            request.CaseName, request.HarnessIndex, ct, pathGuard);
        Input after = await LoadAsync(request.CandidateDirectory, request.NodeId,
            request.CaseName, request.HarnessIndex, ct, pathGuard);
        if (!before.Arguments.SequenceEqual(after.Arguments, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Baseline and candidate argv differ; automatic replay requires the identical workload.");
        }

        if (!string.Equals(before.WorkloadRecipe, after.WorkloadRecipe, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Baseline and candidate harness case recipes differ.");
        }

        if (!string.Equals(before.CpuSet, after.CpuSet, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Baseline and candidate CPU affinity differ.");
        }

        if (string.Equals(before.Executable, after.Executable,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Baseline/candidate reference the same mutable executable path; use independently preserved builds.");
        }

        string backend = request.Backend;
        double duration = request.TimeoutSeconds;
        string destination = Path.GetFullPath(request.ResultsDirectory);
        string output = Path.Combine(destination, "profile-replay-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.CurrentCulture)
                                                                 + "-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(output);
        onRunDirectory?.Invoke(output);
        JsonObject metadata = new()
        {
            ["schemaVersion"] = 1,
            ["nodeId"] = request.NodeId,
            ["case"] = request.CaseName,
            ["harnessIndex"] = request.HarnessIndex,
            ["cpuSet"] = before.CpuSet,
            ["sourceBaseline"] = before.SourceDirectory,
            ["sourceCandidate"] = after.SourceDirectory,
            ["baselineExecutableSha256"] = before.Sha256,
            ["candidateExecutableSha256"] = after.Sha256,
            ["semantics"] =
                "Independent, instrumented replays of verified original run argv; not additional uninstrumented timing samples."
        };
        await File.WriteAllTextAsync(Path.Combine(output, "replay.json"), metadata.ToJsonString() + "\n", ct);
        ProfileWorkflowExecutor executor = new();
        string[] modes = OperatingSystem.IsLinux() ? ["counters", "cpu", "scheduler"]
            : OperatingSystem.IsWindows() ? ["cpu", "scheduler"] : ["cpu"];
        foreach (string? mode in modes)
        {
            foreach ((string? label, Input? item) in new[] { ("before", before), ("after", after) })
            {
                // Check binary identity again immediately before each pass;
                // externally mutated build trees are never silently accepted.
                await using (FileStream stream = File.OpenRead(item.Executable))
                {
                    if (!string.Equals(item.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Executable changed during profiling replay.");
                    }
                }

                string name = $"{(mode == "counters" ? "counters" : mode == "scheduler" ? "scheduler" : "capture")}_{label}";
                // For native harness cases, preserve explicit taskset affinity.
                // perf runs taskset as a child; taskset execs the verified binary.
                string profiledExecutable = item.CpuSet is not null ? "taskset" : item.Executable;
                IReadOnlyList<string> profiledArgs = item.CpuSet is not null
                    ? ["-c", item.CpuSet, item.Executable, .. item.Arguments]
                    : item.Arguments;
                WorkflowOperationResult result = await executor.ExecuteAsync(new WorkflowOperationRequest(
                    "profile", profiledExecutable, profiledArgs, item.WorkingDirectory,
                    new Dictionary<string, string>(), new Dictionary<string, string>
                    {
                        ["backend"] = backend,
                        ["mode"] = mode,
                        ["captureTimeoutSeconds"] = duration.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    },
                    output, name), ct);
                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Profile replay {name} failed with exit code {result.ExitCode}.");
                }
            }
        }

        _ = await ProfileAnalyzer.AnalyzeAsync(output, ct, emitResultPath: false);
        return new ProfileReplayOutcome(1, output, Path.Combine(output, "analysis.json"));
    }
}