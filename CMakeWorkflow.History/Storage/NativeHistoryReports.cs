using System.Globalization;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Toolchains;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core;
using CMakeWorkflow.History.Models;

namespace CMakeWorkflow.History.Storage;

internal static partial class HistoryReports
{
    private const string NativeEvidenceSource = "execution-report.capnp";

    internal static async Task<ParsedRun> ParseNativeWorkflowAsync(NativeExecutionReport terminal, NativeHistoryEvidence evidence,
        Action<string> pathGuard, bool requireNativeProfileAnalysis, CancellationToken ct)
    {
        WorkflowReport report = terminal.Report;
        string directory = Canonical(terminal.RunDirectory);
        pathGuard(directory);
        if (evidence.SchemaVersion != 1 || terminal.OperationId != evidence.OperationId || report.FinishedAt is null ||
            string.IsNullOrWhiteSpace(report.Workflow) || !IsDigest(evidence.DocumentId) || !IsDigest(evidence.BlobId))
        {
            throw new InvalidDataException("Invalid native History report evidence.");
        }

        HostEnvironment? host = report.Environment;
        HostEnvironment? end = report.EnvironmentAtEnd;
        ParsedRun run = new()
        {
            Run = new(Id(directory), directory, "workflow", report.Workflow, report.ManifestHash, HistoryTime(report.StartedAt),
                HistoryTime(report.FinishedAt.Value), report.Status, null, evidence.BlobId)
            {
                SourceSnapshotId = report.SourceSnapshotId,
                HostId = host?.HostId,
                EnvironmentFingerprint = end is not null && (end.HostId != host?.HostId || end.Fingerprint != host?.Fingerprint)
                    ? null : host?.Fingerprint,
                Environment = host is null ? null : new(host.HostId, host.CpuModel, host.Architecture, host.LogicalProcessors,
                    host.AvailableProcessors, host.PhysicalPackages, host.PhysicalCores, host.OperatingSystem, host.KernelVersion,
                    host.Affinity, host.Governor, host.Fingerprint),
                BuildEnvironments = [.. report.BuildEnvironments.Select(build => new HistoryBuildEnvironment(build.Directory,
                    build.Configuration, build.Generator, build.Compiler, build.CompilerId, build.CompilerVersion, build.Flags, build.Fingerprint))]
            },
            NativeEvidence = evidence
        };

        Dictionary<string, string> builds = new(StringComparer.Ordinal);
        Dictionary<string, string> overrides = new(StringComparer.Ordinal);
        foreach (BuildEnvironment build in report.BuildEnvironments)
        {
            builds[build.Directory + "\u001f" + build.Configuration] = build.Fingerprint;
        }

        foreach (WorkflowToolchainIdentity toolchain in report.Toolchains)
        {
            if (toolchain.Linux?.VcpkgPackageTreeSha256 is not { } tree)
            {
                continue;
            }

            string prefix = toolchain.BuildDirectory + "\u001f";
            foreach (string buildKey in builds.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                builds[buildKey] = Hash(Encoding.UTF8.GetBytes(builds[buildKey] + "\n" + tree));
            }
        }

        bool typedProfileAnalysis = requireNativeProfileAnalysis || report.Nodes.Any(node => node.Profile?.Analysis is not null);
        foreach (NodeResult node in report.Nodes)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Status))
            {
                throw new InvalidDataException("Invalid native History node evidence.");
            }

            if (node.EnvironmentOverrideHash is { } environmentOverride)
            {
                overrides[node.Id] = environmentOverride;
            }

            string arguments = JsonSerializer.Serialize(node.Arguments, HistoryJson.Default.ListString);
            string? scenario = Scenario(node.Arguments);
            if (node.Kind is "profile" or "profile_snapshot")
            {
                run.ProfileInputs[node.Id] = (arguments, scenario);
            }

            run.Nodes.Add(new(run.Run.Id, node.Id, node.Kind, node.Status, node.DurationSeconds,
                JsonSerializer.Serialize(node.Dependencies, HistoryJson.Default.ListString), node.BuildDirectory,
                node.Configuration, node.Target));
            AddNativeMeasurements(run, node, arguments, scenario);
            await ImportDagFlowHarnessAsync(directory, run, node.Id, node.Outputs.ContainsKey,
                name => node.Outputs.GetValueOrDefault(name), pathGuard, ct);
            AddArtifact(run, node.Id, "stdout", node.Stdout);
            AddArtifact(run, node.Id, "stderr", node.Stderr);
            if (node.Artifact is { } executable)
            {
                AddArtifact(run, node.Id, "executable", executable.Executable, executable.Sha256);
            }
            else if (node.Kind is "run" or "profile" && node.Executable is { } path && Path.IsPathFullyQualified(path))
            {
                AddArtifact(run, node.Id, "executable", path, node.ExecutableSha256);
            }

            if (typedProfileAnalysis && node is { Kind: "profile" or "profile_snapshot", Status: "succeeded", Profile: null })
            {
                throw new InvalidDataException("A successful native profiler node is missing its capture evidence.");
            }

            if (node.Profile is { } profile)
            {
                AddArtifact(run, profile.CaptureId, "trace", profile.RawTrace, profile.RawTraceSha256);
                AddArtifact(run, profile.CaptureId, "pmc-configuration", profile.CounterConfiguration,
                    profile.CounterConfigurationSha256);
                if (typedProfileAnalysis)
                {
                    WorkflowProfileAnalysis analysis = profile.Analysis ??
                        throw new InvalidDataException("Native profiler capture lacks structured analysis evidence.");
                    if (analysis.Status == "unavailable")
                    {
                        throw new InvalidDataException("The native profiler capture could not be analyzed.");
                    }

                    (string? workload, string? profileScenario) = run.ProfileInputs.GetValueOrDefault(node.Id);
                    foreach (WorkflowProfileMeasurement sample in analysis.Measurements)
                    {
                        string? capture = profile is { Backend: "perf", Mode: "counters" } &&
                            profile.CaptureId.StartsWith("counters_", StringComparison.Ordinal) ? null : profile.CaptureId;
                        run.Measurements.Add(new(run.Run.Id, capture, sample.Name, sample.Unit,
                            sample.Value, sample.Variant, profileScenario, NativeEvidenceSource, sample.Method, 0, workload, null));
                    }
                }
            }
        }

        foreach ((string build, string? configuration, string? kit, string? toolchain) in report.Nodes.Select(node => (node.BuildDirectory, node.Configuration,
                     node.KitLockSha256, node.ToolchainFingerprint))
                     .Where(node => node.KitLockSha256 is not null && node.ToolchainFingerprint is not null).Distinct())
        {
            string key = build + "\u001f" + configuration;
            builds[key] = Hash(Encoding.UTF8.GetBytes(builds.GetValueOrDefault(key) + "\n" + kit + "\n" + toolchain));
        }

        if (!typedProfileAnalysis)
        {
            await ImportProfileAsync(directory, run, pathGuard, ct, includeCaptureJson: false);
        }

        CompleteWorkflowImport(run, directory, NativeEvidenceSource, builds, overrides, pathGuard);
        return run;
    }

    private static bool IsDigest(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);
    private static string HistoryTime(DateTimeOffset time) => time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static void AddNativeMeasurements(ParsedRun run, NodeResult node, string arguments, string? scenario)
    {
        string id = node.Id;
        string? variant = TimingVariant(id);
        if (node.ProcessResources is { SchemaVersion: 1, Samples: > 0, SampleIntervalMilliseconds: > 0 } resources &&
            resources.Scope is "visible-process-tree" or "direct-process" && !string.IsNullOrEmpty(resources.Method))
        {
            string method = resources.Method + "; scope=" + resources.Scope + "; interval-ms=" +
                resources.SampleIntervalMilliseconds.ToString(CultureInfo.InvariantCulture);
            Add("peak_rss_bytes", "B", resources.PeakRssBytes, variant, scenario, method, arguments);
            Add("cpu_time", "s", resources.CpuSeconds, variant, scenario, method, arguments);
        }

        if (node is { Kind: "run", Status: "succeeded" })
        {
            if (node.ProcessDurationSeconds is { } duration && double.IsFinite(duration))
            {
                int index = (node.BenchmarkTrial is null ? null : TimingSampleIndex(id)) ??
                    run.Measurements.Count(m => m.Metric == "process_wall_time" && m.Variant == variant);
                run.Measurements.Add(new(run.Run.Id, id, "process_wall_time", "s", duration, variant, scenario,
                    NativeEvidenceSource, "uninstrumented process wall time", index, arguments, null)
                {
                    BenchmarkTrial = node.BenchmarkTrial
                });
            }

            if (node.Metrics is { } metrics)
            {
                AddNativeBenchmarkMeasurements(run, node.BenchmarkTrial, metrics, id, variant, scenario, arguments);
            }
        }

        if (node.Status != "succeeded" || node.Kind is not ("build" or "invoke"))
        {
            return;
        }

        if (node.Kind == "build" || node.CompilerCache is not null)
        {
            Add("build_wall_time", "s", node.ProcessDurationSeconds, id, node.Target, "native build process wall time");
        }

        if (node.CompilerCache is { SchemaVersion: 1, Provider: "ccache" or "sccache" } cache)
        {
            if (cache.Scope is "build-attempt" or "shared-server")
            {
                string method = $"{cache.Provider} {cache.Scope} counters";
                Add("compiler_cache_hits", "count", cache.Hits, id, node.Target, method);
                Add("compiler_cache_misses", "count", cache.Misses, id, node.Target, method);
                Add("compiler_cache_remote_read_hits", "count", cache.RemoteReadHits, id, node.Target, method);
                Add("compiler_cache_remote_writes", "count", cache.RemoteWrites, id, node.Target, method);
                Add("compiler_cache_reported_work_time", "s", cache.CompilerWorkSeconds, id, node.Target, method);
            }

            const string telemetry = "per-attempt backend telemetry";
            Add("compiler_cache_bytes_read", "B", cache.BytesRead, id, node.Target, telemetry);
            Add("compiler_cache_bytes_written", "B", cache.BytesWritten, id, node.Target, telemetry);
            Add("compiler_time", "s", cache.CompileSeconds, id, node.Target, telemetry);
        }

        void Add(string metric, string unit, double? value, string? measurementVariant, string? measurementScenario,
            string method, string? workload = null)
        {
            if (value is double number && double.IsFinite(number) && number >= 0)
            {
                run.Measurements.Add(new(run.Run.Id, id, metric, unit, number, measurementVariant, measurementScenario,
                    NativeEvidenceSource, method, 0, workload, null));
            }
        }
    }

    private static void AddNativeBenchmarkMeasurements(ParsedRun run, BenchmarkTrial? trial, IReadOnlyList<BenchmarkMetricSample> metrics,
        string id, string? variant, string? scenario, string arguments)
    {
        if (metrics.Count is < 1 or > 32)
        {
            throw new InvalidDataException($"Invalid benchmark metrics for {id}.");
        }

        if (variant is not null)
        {
            int separator = variant.LastIndexOf('_');
            if (separator > 0 && variant.Length - separator == 5 &&
                int.TryParse(variant.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                string combination = variant[(separator + 1)..];
                scenario = scenario is null ? "case_" + combination : $"{scenario} [{combination}]";
                variant = variant[..separator];
            }
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (BenchmarkMetricSample sample in metrics)
        {
            if (sample.Name.Length == 0 || !names.Add(sample.Name) || sample.Unit.Length == 0 || sample.Path.Length == 0 ||
                sample.Direction is not ("lower-is-better" or "higher-is-better") || !double.IsFinite(sample.Value))
            {
                throw new InvalidDataException($"Invalid benchmark metric for {id}.");
            }

            run.Measurements.Add(new(run.Run.Id, id, sample.Name, sample.Unit, sample.Value, variant, scenario,
                NativeEvidenceSource, "benchmark stdout JSON: " + sample.Path, TimingSampleIndex(id) ?? 0, arguments, null)
            {
                Direction = sample.Direction,
                BenchmarkTrial = trial
            });
        }
    }
}
