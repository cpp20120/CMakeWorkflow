using System.ComponentModel;
using System.Text.Json;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Core;

namespace CMakeWorkflow.CMake.Build;

internal sealed class CompilerCacheMeasurement(CompilerCacheProfile profile, string output, string logStem,
    string workingDirectory, IReadOnlyDictionary<string, string> inherited)
{
    private sealed record ServerCounters(long Requests, long Hits, long Misses, double? CompilerWorkSeconds);

    private readonly string statsLog = Path.Combine(Path.GetFullPath(output), logStem + ".ccache.stats");
    private readonly string telemetry = Path.Combine(Path.GetFullPath(output), logStem + ".cache-transfer.json");
    private ServerCounters? before;

    internal Dictionary<string, string> Environment { get; } = profile.EnvironmentFor(inherited);

    internal async Task StartAsync(CancellationToken ct)
    {
        if (profile.ExternalTelemetry)
        {
            Environment["CMAKEWORKFLOW_COMPILER_CACHE_METRICS"] = telemetry;
        }

        if (profile.Provider == "ccache")
        {
            Environment["CCACHE_STATSLOG"] = statsLog;
        }
        else
        {
            before = await ReadSccacheAsync(ct);
        }
    }

    internal async Task<CompilerCacheStatistics> FinishAsync(CancellationToken ct)
    {
        CompilerCacheStatistics result = new(1, profile.Name, profile.Provider, "unavailable");
        try
        {
            if (profile.Provider == "ccache")
            {
                result = ReadCcache(result, ct);
            }
            else if (before is { } first && await ReadSccacheAsync(ct) is { } last
                     && last.Requests >= first.Requests && last.Hits >= first.Hits && last.Misses >= first.Misses)
            {
                result = result with
                {
                    Scope = "shared-server",
                    Hits = last.Hits - first.Hits,
                    Misses = last.Misses - first.Misses,
                    CompilerWorkSeconds = last.CompilerWorkSeconds is { } end
                        && first.CompilerWorkSeconds is { } start && end >= start ? end - start : null
                };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Diagnostics cannot replace the actual build outcome.
        }

        return profile.ExternalTelemetry ? await ReadTelemetryAsync(result, ct) : result;
    }

    private CompilerCacheStatistics ReadCcache(CompilerCacheStatistics result, CancellationToken ct)
    {
        if (!File.Exists(statsLog))
        {
            return result;
        }

        long hits = 0, misses = 0, remoteHits = 0, remoteWrites = 0;
        bool observed = false;
        foreach (string line in File.ReadLines(statsLog))
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            observed = true;
            switch (line)
            {
                case "direct_cache_hit":
                case "preprocessed_cache_hit":
                    hits++;
                    break;
                case "cache_miss":
                    misses++;
                    break;
                case "remote_storage_read_hit":
                    remoteHits++;
                    break;
                case "remote_storage_write":
                    remoteWrites++;
                    break;
                default:
                    break;
            }
        }

        return observed
            ? result with
            {
                Scope = "build-attempt",
                Hits = hits,
                Misses = misses,
                RemoteReadHits = remoteHits,
                RemoteWrites = remoteWrites
            }
            : result;
    }

    private async Task<ServerCounters?> ReadSccacheAsync(CancellationToken ct)
    {
        try
        {
            CommandResult command = await CommandExecution.RunAsync(profile.Executable,
                ["--show-stats", "--stats-format=json"], workingDirectory, TimeSpan.FromSeconds(3),
                Environment.ToDictionary(x => x.Key, x => (string?)x.Value, StringComparer.OrdinalIgnoreCase), ct);
            if (command.ExitCode != 0 || command.TimedOut || command.Stdout is null)
            {
                return null;
            }

            using JsonDocument data = JsonDocument.Parse(command.Stdout);
            JsonElement stats = data.RootElement.GetProperty("stats");
            return new(stats.GetProperty("compile_requests").GetInt64(), Count(stats, "cache_hits"),
                Count(stats, "cache_misses"), Duration(stats));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception
                                      or JsonException or KeyNotFoundException or InvalidOperationException
                                      or FormatException or OverflowException)
        {
            return null;
        }
    }

    private async Task<CompilerCacheStatistics> ReadTelemetryAsync(CompilerCacheStatistics result, CancellationToken ct)
    {
        try
        {
            FileInfo file = new(telemetry);
            if (!file.Exists || file.LinkTarget is not null || file.Length > 4096)
            {
                return result;
            }

            using JsonDocument data = JsonDocument.Parse(await File.ReadAllBytesAsync(telemetry, ct));
            JsonElement root = data.RootElement;
            if (NonnegativeInteger(root, "schemaVersion") != 1)
            {
                return result;
            }

            double? seconds = root.TryGetProperty("compileSeconds", out JsonElement time)
                && time.ValueKind == JsonValueKind.Number && time.TryGetDouble(out double duration)
                && double.IsFinite(duration) && duration >= 0 ? duration : null;
            return result with
            {
                BytesRead = NonnegativeInteger(root, "bytesRead"),
                BytesWritten = NonnegativeInteger(root, "bytesWritten"),
                CompileSeconds = seconds
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException)
        {
            return result;
        }
    }

    private static long Count(JsonElement stats, string key) =>
        stats.GetProperty(key).GetProperty("counts").EnumerateObject().Sum(x => x.Value.GetInt64());

    private static double? Duration(JsonElement stats)
    {
        return stats.TryGetProperty("compiler_write_duration", out JsonElement duration)
            ? duration.GetProperty("secs").GetInt64() + (duration.GetProperty("nanos").GetInt64() / 1_000_000_000d)
            : null;
    }

    private static long? NonnegativeInteger(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed) && parsed >= 0 ? parsed : null;
}
