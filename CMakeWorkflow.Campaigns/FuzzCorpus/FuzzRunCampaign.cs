using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.Campaigns;

public sealed record FuzzRunOptions(string BuildDirectory, string Layer, string? Backend,
    string? CorpusDirectory, string ResultsDirectory, string? Configuration,
    int DurationSeconds, int InputTimeoutSeconds, int MaxLength, bool ReplayOnly);

public static class FuzzRunCampaign
{
    public static async Task<int> RunAsync(FuzzRunOptions options, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.Layer, "^[a-z][a-z0-9_]*$"))
            throw new ArgumentException("--layer must be a DagFlow fuzz layer name.");
        if (options.DurationSeconds is < 1 or > 86400 || options.InputTimeoutSeconds is < 1 or > 3600
            || options.MaxLength is < 1 or > 1048576)
            throw new ArgumentException("Fuzz duration, input timeout and max length must be positive and bounded.");
        var buildDirectory = Path.GetFullPath(options.BuildDirectory);
        var targetName = "dagflow_fuzz_" + options.Layer;
        var observed = await CMakeInspection.InspectAsync(buildDirectory, ct);
        if (!observed.HasCodeModel || observed.SourceDirectory is null)
            throw new InvalidOperationException("Fuzz run requires a configured CMake File API build tree.");
        var backend = CacheValue(buildDirectory, "DAGFLOW_FUZZ_BACKEND")
            ?? throw new InvalidOperationException("Build tree has no DAGFLOW_FUZZ_BACKEND cache entry.");
        if (backend is not ("libfuzzer" or "aflpp" or "honggfuzz"))
            throw new InvalidOperationException($"Unsupported configured fuzz backend: {backend}");
        if (options.Backend is not null && options.Backend != backend)
            throw new InvalidOperationException($"Requested backend {options.Backend} disagrees with configured {backend}.");
        var target = CMakeInspection.Resolve(new RequestedBuildContext(buildDirectory, options.Configuration),
            observed, targetName);
        CMakeInspection.RequireTarget(observed, target, executableOnly: true);
        using var ownership = await BuildTreeOwnership.AcquireAsync([buildDirectory], ct);
        var build = CMakeBuildCommand.Create(target);
        var buildResult = await CommandExecution.RunAsync(observed.CMakeExecutable ?? "cmake",
            build.Arguments, build.WorkingDirectory, TimeSpan.FromMinutes(20), null, ct);
        RequireSuccess(buildResult, "CMake build");
        var artifact = await CMakeFileApi.ResolveExecutableAsync(target, ct);
        var executable = artifact.ExecutablePath;
        var executableHash = await CommandExecution.Sha256Async(executable, ct);

        var run = Path.Combine(Path.GetFullPath(options.ResultsDirectory),
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N"));
        var corpus = Path.Combine(run, "corpus");
        var artifacts = Path.Combine(run, "artifacts");
        Directory.CreateDirectory(corpus);
        Directory.CreateDirectory(artifacts);
        await File.WriteAllTextAsync(Path.Combine(run, "build.log"), buildResult.Output, ct);
        var sourceCorpus = options.CorpusDirectory is null
            ? Path.Combine(observed.SourceDirectory, "fuzz", "corpus", options.Layer)
            : Path.GetFullPath(options.CorpusDirectory);
        CopyInputs(sourceCorpus, corpus);
        if (options.CorpusDirectory is null)
            CopyInputs(Path.Combine(buildDirectory, targetName + "-corpus"), corpus);
        if (Directory.GetFiles(corpus).Length == 0)
            throw new InvalidOperationException("Fuzz corpus is empty; supply --corpus or initialize seeds.");
        var baseline = await InventoryAsync(corpus, ct);
        await WriteInventoryAsync(Path.Combine(run, "initial-corpus.json"), baseline, ct);
        var report = new FuzzRunReport
        {
            SchemaVersion = 1, Layer = options.Layer, Backend = backend,
            BuildDirectory = buildDirectory, Target = targetName,
            Executable = executable, ExecutableSha256 = executableHash,
            SourceCorpus = sourceCorpus, StartedAt = DateTimeOffset.UtcNow,
            InitialInputs = baseline.Count, ReplayOnly = options.ReplayOnly,
            DurationSeconds = options.DurationSeconds, InputTimeoutSeconds = options.InputTimeoutSeconds,
            MaxLength = options.MaxLength,
            BuildArguments = build.Arguments.ToList()
        };
        Console.WriteLine($"Fuzz run: {run}");
        try
        {
            var inputs = Directory.GetFiles(corpus).Order(StringComparer.Ordinal).ToArray();
            if (backend == "libfuzzer")
            {
                foreach (var input in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await CommandExecution.RunAsync(executable,
                        [input, "-runs=1", $"-timeout={options.InputTimeoutSeconds}",
                            $"-artifact_prefix={artifacts}{Path.DirectorySeparatorChar}"],
                        run, TimeSpan.FromSeconds(options.InputTimeoutSeconds + 30), null, ct);
                    report.ReplayedInputs++;
                    if (result.ExitCode != 0 || result.TimedOut)
                    {
                        await File.WriteAllTextAsync(Path.Combine(run, "replay-failure.log"), result.Output, ct);
                        if (IsInfrastructureFailure(result.Output))
                        {
                            report.InfrastructureError = true;
                            report.Error = "LeakSanitizer cannot run in this environment; see replay-failure.log.";
                        }
                        else report.FailedInputs.Add(Path.GetFileName(input));
                        break;
                    }
                }
            }
            else if (backend == "aflpp")
            {
                var map = Path.Combine(run, "replay-map");
                foreach (var input in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await CommandExecution.RunAsync("afl-showmap",
                        ["-q", "-o", map, "--", executable, input], run,
                        TimeSpan.FromSeconds(options.InputTimeoutSeconds + 30), null, ct);
                    report.ReplayedInputs++;
                    if (result.ExitCode != 0 || result.TimedOut)
                    {
                        report.FailedInputs.Add(Path.GetFileName(input));
                        await File.WriteAllTextAsync(Path.Combine(run, "replay-failure.log"), result.Output, ct);
                        break;
                    }
                }
            }
            else
            {
                report.ReplayMechanism = "honggfuzz-native";
                if (options.ReplayOnly)
                {
                    var result = await CommandExecution.RunAsync("honggfuzz",
                        ["-P", "-i", corpus, "--workspace", Path.Combine(artifacts, "honggfuzz-replay"),
                            "--run_time", "1", "--iterations", inputs.Length.ToString(), "--", executable],
                        run, TimeSpan.FromSeconds(Math.Max(30, inputs.Length * options.InputTimeoutSeconds)), null, ct);
                    await File.WriteAllTextAsync(Path.Combine(run, "replay.log"), result.Output, ct);
                    report.ReplayedInputs = inputs.Length;
                    if (result.ExitCode != 0 || result.TimedOut)
                        report.FailedInputs.Add("honggfuzz-native-replay");
                }
            }
            if (report.FailedInputs.Count == 0 && !report.InfrastructureError && !options.ReplayOnly)
            {
                var (tool, args, environment) = CampaignCommandLine(backend, executable, corpus,
                    artifacts, options.DurationSeconds, options.InputTimeoutSeconds, options.MaxLength);
                report.CampaignTool = tool;
                report.CampaignArguments = args.ToList();
                var result = await CommandExecution.RunAsync(tool, args, run,
                    TimeSpan.FromSeconds(options.DurationSeconds + 120), environment, ct);
                await File.WriteAllTextAsync(Path.Combine(run, "campaign.log"), result.Output, ct);
                report.CampaignExitCode = result.ExitCode;
                report.CampaignTimedOut = result.TimedOut;
            }
            var after = await InventoryAsync(corpus, ct);
            await WriteInventoryAsync(Path.Combine(run, "final-corpus.json"), after, ct);
            report.FinalInputs = after.Count;
            var known = baseline.Values.ToHashSet(StringComparer.Ordinal);
            report.NewInputs = after.Where(x => !known.Contains(x.Value))
                .Select(x => x.Key).Order(StringComparer.Ordinal).ToList();
            report.CrashArtifacts = Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories)
                .Where(IsCrashArtifact)
                .Select(x => Path.GetRelativePath(run, x)).Order(StringComparer.Ordinal).ToList();
            foreach (var crashArtifact in report.CrashArtifacts)
                report.ArtifactSha256[crashArtifact] = await CommandExecution.Sha256Async(
                    Path.Combine(run, crashArtifact), ct);
            report.Status = report.FailedInputs.Count == 0 && !report.InfrastructureError
                && (report.CampaignExitCode is null or 0)
                && !report.CampaignTimedOut && report.CrashArtifacts.Count == 0 ? "succeeded" : "failed";
        }
        catch (OperationCanceledException)
        {
            report.Status = "canceled";
            throw;
        }
        catch (Exception error)
        {
            report.Status = "failed";
            report.Error = error.Message;
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.UtcNow;
            await WriteReportAsync(run, report);
        }
        Console.WriteLine($"{report.Status}: replayed {report.ReplayedInputs}, corpus {report.InitialInputs} -> {report.FinalInputs}, new {report.NewInputs.Count}, artifacts {report.CrashArtifacts.Count}");
        return report.Status == "succeeded" ? 0 : 1;
    }

    public static async Task<int> TriageAsync(string runDirectory, CancellationToken ct)
    {
        var run = Path.GetFullPath(runDirectory);
        var report = JsonSerializer.Deserialize(await File.ReadAllTextAsync(
            Path.Combine(run, "report.json"), ct), FuzzJson.Default.FuzzRunReport)
            ?? throw new IOException("Invalid fuzz report.");
        if (report.InfrastructureError)
            throw new InvalidOperationException(report.Error ?? "Fuzz run failed because the execution environment was unavailable.");
        if (await CommandExecution.Sha256Async(report.Executable, ct) != report.ExecutableSha256)
            throw new InvalidOperationException("Fuzzer executable changed since the campaign; rebuild or keep the original binary.");
        if (report.FailedInputs.Count > 0)
            await VerifyCorpusSnapshotAsync(run, Path.Combine(run, "corpus"), ct);
        var findings = new System.Text.Json.Nodes.JsonArray();
        var unreproduced = 0;
        var candidates = report.CrashArtifacts.Concat(report.FailedInputs
            .Where(x => File.Exists(Path.Combine(run, "corpus", x)))
            .Select(x => Path.Combine("corpus", x)))
            .Distinct(StringComparer.Ordinal).ToArray();
        var logDirectory = Path.Combine(run, "triage-logs");
        Directory.CreateDirectory(logDirectory);
        foreach (var artifact in candidates)
        {
            var path = Path.GetFullPath(Path.Combine(run, artifact));
            if (!path.StartsWith(run + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException($"Triage artifact escapes the run directory: {artifact}");
            if (!File.Exists(path)) throw new FileNotFoundException("Crash artifact is missing.", path);
            if (report.ArtifactSha256.TryGetValue(artifact, out var expectedArtifactHash)
                && await CommandExecution.Sha256Async(path, ct) != expectedArtifactHash)
                throw new IOException($"Crash artifact changed after the campaign: {artifact}");
            CommandResult result;
            var reproduced = false;
            if (report.Backend == "libfuzzer")
            {
                result = await CommandExecution.RunAsync(report.Executable,
                    [path, "-runs=1", $"-timeout={report.InputTimeoutSeconds}"], run,
                    TimeSpan.FromSeconds(report.InputTimeoutSeconds + 30), null, ct);
                reproduced = (result.ExitCode != 0 || result.TimedOut)
                    && !IsInfrastructureFailure(result.Output);
            }
            else if (report.Backend == "aflpp")
            {
                result = await CommandExecution.RunAsync("afl-showmap",
                    ["-q", "-o", Path.Combine(run, "triage-map"), "--", report.Executable, path],
                    run, TimeSpan.FromSeconds(report.InputTimeoutSeconds + 30), null, ct);
                reproduced = result.ExitCode != 0 || result.TimedOut;
            }
            else if (report.Backend == "honggfuzz")
            {
                var single = Path.Combine(run, "triage-input");
                Directory.CreateDirectory(single);
                File.Copy(path, Path.Combine(single, Path.GetFileName(path)), overwrite: true);
                var workspace = Path.Combine(run, "triage-honggfuzz", Guid.NewGuid().ToString("N"));
                result = await CommandExecution.RunAsync("honggfuzz",
                    ["-P", "-i", single, "--workspace", workspace, "--run_time", "1",
                        "--iterations", "1", "--", report.Executable], run,
                    TimeSpan.FromSeconds(report.InputTimeoutSeconds + 30), null, ct);
                reproduced = Directory.Exists(workspace)
                    && Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Any(IsCrashArtifact);
                Directory.Delete(single, recursive: true);
            }
            else throw new InvalidOperationException($"Unknown fuzz backend: {report.Backend}");
            if (!reproduced) unreproduced++;
            var logName = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(artifact))).ToLowerInvariant()[..16] + ".log";
            var logPath = Path.Combine(logDirectory, logName);
            await File.WriteAllTextAsync(logPath, result.Output, ct);
            findings.Add((System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
            {
                ["artifact"] = artifact,
                ["sha256"] = await CommandExecution.Sha256Async(path, ct),
                ["reproduced"] = reproduced,
                ["infrastructureError"] = IsInfrastructureFailure(result.Output),
                ["exitCode"] = result.ExitCode,
                ["timedOut"] = result.TimedOut,
                ["log"] = Path.GetRelativePath(run, logPath)
            });
        }
        var output = Path.Combine(run, "triage.json");
        await File.WriteAllTextAsync(output, findings.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
        Console.WriteLine($"Fuzz triage: {output}");
        return unreproduced == 0 ? 0 : 1;
    }

    public static Task<int> MergeAsync(string runDirectory, string? outputDirectory, CancellationToken ct) =>
        MergeAsync(runDirectory, outputDirectory, coverage: false, ct: ct);

    public static async Task<int> MergeAsync(string runDirectory, string? outputDirectory,
        bool coverage, CancellationToken ct)
    {
        var run = Path.GetFullPath(runDirectory);
        var report = JsonSerializer.Deserialize(await File.ReadAllTextAsync(
            Path.Combine(run, "report.json"), ct), FuzzJson.Default.FuzzRunReport)
            ?? throw new IOException("Invalid fuzz report.");
        if (report.Status != "succeeded")
            throw new InvalidOperationException("Only a successful fuzz run can be merged; triage failed runs first.");
        var source = Path.Combine(run, "corpus");
        await VerifyCorpusSnapshotAsync(run, source, ct);
        var destination = Path.GetFullPath(outputDirectory
            ?? Path.Combine(report.BuildDirectory, report.Target + "-corpus"));
        using var ownership = outputDirectory is null
            ? await BuildTreeOwnership.AcquireAsync([report.BuildDirectory], ct) : null;
        Directory.CreateDirectory(destination);
        var known = (await InventoryAsync(destination, ct)).Values.ToHashSet(StringComparer.Ordinal);
        var imported = 0;
        if (coverage)
        {
            if (report.Backend != "libfuzzer")
                throw new NotSupportedException("Coverage-guided corpus merge requires libFuzzer.");
            if (await CommandExecution.Sha256Async(report.Executable, ct) != report.ExecutableSha256)
                throw new InvalidOperationException("Fuzzer executable changed since the campaign.");
            var result = await CommandExecution.RunAsync(report.Executable,
                ["-merge=1", $"-timeout={report.InputTimeoutSeconds}", destination, source],
                run, TimeSpan.FromMinutes(30), null, ct);
            await File.WriteAllTextAsync(Path.Combine(run, "coverage-merge.log"), result.Output, ct);
            RequireSuccess(result, "libFuzzer coverage merge");
            var after = (await InventoryAsync(destination, ct)).Values.ToHashSet(StringComparer.Ordinal);
            imported = after.Except(known, StringComparer.Ordinal).Count();
        }
        else
        {
            foreach (var file in Directory.GetFiles(source).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var hash = await CommandExecution.Sha256Async(file, ct);
                if (!known.Add(hash)) continue;
                var name = Path.GetFileName(file);
                var target = Path.Combine(destination, name);
                if (File.Exists(target)) target = Path.Combine(destination, name + "-" + hash[..12]);
                if (File.Exists(target))
                    throw new IOException($"Corpus destination name collision: {target}");
                File.Copy(file, target);
                if (await CommandExecution.Sha256Async(target, ct) != hash)
                    throw new IOException($"Corpus copy hash mismatch: {target}");
                imported++;
            }
        }
        var summary = new System.Text.Json.Nodes.JsonObject
        {
            ["schemaVersion"] = 1, ["runDirectory"] = run,
            ["destination"] = destination, ["imported"] = imported,
            ["mode"] = coverage ? "libfuzzer-coverage" : "sha256",
            ["totalInputs"] = Directory.GetFiles(destination).Length
        };
        var output = Path.Combine(run, "merge.json");
        await File.WriteAllTextAsync(output, summary.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
        Console.WriteLine($"Fuzz corpus: {imported} imported into {destination}");
        return 0;
    }

    private static (string Tool, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string?>? Environment)
        CampaignCommandLine(string backend, string executable, string corpus, string artifacts,
            int duration, int timeout, int maxLength) => backend switch
        {
            "libfuzzer" => (executable, [corpus, $"-max_total_time={duration}",
                $"-timeout={timeout}", $"-max_len={maxLength}",
                $"-artifact_prefix={artifacts}{Path.DirectorySeparatorChar}"], null),
            "aflpp" => ("afl-fuzz", ["-i", corpus, "-o", Path.Combine(artifacts, "afl"),
                "-V", duration.ToString(), "-G", maxLength.ToString(), "--", executable],
                new Dictionary<string, string?> { ["AFL_NO_UI"] = "1" }),
            "honggfuzz" => ("honggfuzz", ["-P", "-i", corpus, "--workspace",
                Path.Combine(artifacts, "honggfuzz"), "--run_time", duration.ToString(),
                "--max_file_size", maxLength.ToString(), "--", executable], null),
            _ => throw new ArgumentException($"Unknown fuzz backend: {backend}")
        };

    private static void CopyInputs(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.GetFiles(source).Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(file);
            var name = Path.GetFileName(file);
            var target = Path.Combine(destination, name);
            if (File.Exists(target) && !File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                    .ToLowerInvariant()[..12];
                target = Path.Combine(destination, name + "-" + hash);
            }
            if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
        }
    }

    private static bool IsCrashArtifact(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) return false;
        return name.StartsWith("crash-", StringComparison.Ordinal)
            || name.StartsWith("timeout-", StringComparison.Ordinal)
            || name.StartsWith("oom-", StringComparison.Ordinal)
            || name.StartsWith("SIG", StringComparison.Ordinal)
            || Path.GetDirectoryName(path)!.Split(Path.DirectorySeparatorChar)
                .Contains("crashes", StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsInfrastructureFailure(string output) =>
        output.Contains("LeakSanitizer has encountered a fatal error", StringComparison.Ordinal)
        && output.Contains("LeakSanitizer does not work under ptrace", StringComparison.Ordinal);

    private static async Task<Dictionary<string, string>> InventoryAsync(string directory, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(directory))
            result[Path.GetFileName(file)] = await CommandExecution.Sha256Async(file, ct);
        return result;
    }

    private static Task WriteInventoryAsync(string path, IReadOnlyDictionary<string, string> inventory,
        CancellationToken ct)
    {
        var items = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, hash) in inventory.OrderBy(x => x.Key, StringComparer.Ordinal))
            items[name] = hash;
        return File.WriteAllTextAsync(path, items.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }) + "\n", ct);
    }

    private static async Task VerifyCorpusSnapshotAsync(string run, string corpus, CancellationToken ct)
    {
        var manifest = Path.Combine(run, "final-corpus.json");
        if (!File.Exists(manifest))
            throw new IOException("Fuzz run has no final corpus SHA-256 inventory.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, ct));
        var expected = document.RootElement.EnumerateObject()
            .ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal);
        var actual = await InventoryAsync(corpus, ct);
        if (expected.Count != actual.Count || expected.Any(x => !actual.TryGetValue(x.Key, out var hash)
            || hash != x.Value))
            throw new IOException("Fuzz corpus changed after the campaign; refusing to merge it.");
    }

    private static string? CacheValue(string buildDirectory, string key)
    {
        var cache = Path.Combine(buildDirectory, "CMakeCache.txt");
        if (!File.Exists(cache)) return null;
        foreach (var line in File.ReadLines(cache))
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
                return line[(line.IndexOf('=') + 1)..];
        return null;
    }

    private static void RequireSuccess(CommandResult result, string operation)
    {
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed: {result.Output}");
    }

    private static Task WriteReportAsync(string run, FuzzRunReport report) =>
        File.WriteAllTextAsync(Path.Combine(run, "report.json"),
            JsonSerializer.Serialize(report, FuzzJson.Default.FuzzRunReport) + "\n",
            CancellationToken.None);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FuzzRunReport))]
internal partial class FuzzJson : JsonSerializerContext;

public sealed class FuzzRunReport
{
    public int SchemaVersion { get; set; }
    public string Layer { get; set; } = "";
    public string Backend { get; set; } = "";
    public string BuildDirectory { get; set; } = "";
    public string Target { get; set; } = "";
    public string Executable { get; set; } = "";
    public string ExecutableSha256 { get; set; } = "";
    public string SourceCorpus { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public int InitialInputs { get; set; }
    public int FinalInputs { get; set; }
    public int ReplayedInputs { get; set; }
    public List<string> FailedInputs { get; set; } = [];
    public List<string> NewInputs { get; set; } = [];
    public List<string> CrashArtifacts { get; set; } = [];
    public Dictionary<string, string> ArtifactSha256 { get; set; } = [];
    public bool ReplayOnly { get; set; }
    public string ReplayMechanism { get; set; } = "direct-input";
    public int DurationSeconds { get; set; }
    public int InputTimeoutSeconds { get; set; }
    public int MaxLength { get; set; }
    public List<string> BuildArguments { get; set; } = [];
    public string? CampaignTool { get; set; }
    public List<string> CampaignArguments { get; set; } = [];
    public int? CampaignExitCode { get; set; }
    public bool CampaignTimedOut { get; set; }
    public bool InfrastructureError { get; set; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
}
