using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CMakeWorkflow.History.Storage;

/// <summary>Imports only completed, hash-bound schedule-v1 historical captures; legacy archives retain their interpretation.</summary>
/// <remarks>
/// Seed, block and pair identities remain in each workload recipe. They are ordering factors, not extra
/// independent observations. Import performs no scheduling and does not infer how an older archive was ordered.
/// </remarks>
internal static class HistoricalHistoryReports
{
    private static readonly string[] WorkloadFields = ["seed", "prng", "prng_version", "schedule_strategy",
        "shuffle_scope", "measurement_set_sha256", "block_id", "pair_id", "workers", "scenario"];
    /// <summary>Verifies completion hashes and matches every raw row to its saved block, pair and order.</summary>
    /// <remarks>Builds detached history data; the store commits only after the full import batch parses successfully.</remarks>
    internal static async Task<ParsedRun?> ParseAsync(string directory, CancellationToken ct)
    {
        string markerPath = Path.Combine(directory, "historical-completion.json");
        byte[] markerBytes = await File.ReadAllBytesAsync(markerPath, ct);
        using JsonDocument marker = JsonDocument.Parse(markerBytes);
        JsonElement completion = marker.RootElement;
        if (completion.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported historical completion schema.");
        }
        if (completion.GetProperty("status").GetString() != "succeeded") { return null; }
        string[] names = ["manifest.json", "schedule.json", "timings.jsonl", "counters.jsonl", "summary.json"];
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        byte[] manifestBytes = await ReadVerifiedAsync("manifest.json");
        byte[] scheduleBytes = await ReadVerifiedAsync("schedule.json");
        _ = await ReadVerifiedAsync("summary.json");

        using JsonDocument manifestDocument = JsonDocument.Parse(manifestBytes);
        using JsonDocument scheduleDocument = JsonDocument.Parse(scheduleBytes);
        JsonElement manifest = manifestDocument.RootElement;
        JsonElement schedule = scheduleDocument.RootElement;
        JsonElement identity = schedule.GetProperty("identity");
        if (schedule.GetProperty("schemaVersion").GetInt32() != 1 ||
            schedule.GetProperty("protocol").GetString() != "dagflow-historical-schedule/v1" ||
            !string.Equals(manifest.GetProperty("schedule_sha256").GetString(), hashes["schedule.json"], StringComparison.OrdinalIgnoreCase) ||
            manifest.GetProperty("scheduling").GetProperty("identity").GetRawText() != identity.GetRawText())
        {
            throw new InvalidDataException("Historical manifest/schedule identity mismatch.");
        }
        string runId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
        ParsedRun run = new()
        {
            Run = new(runId, directory, "dagflow-historical", "dagflow-stress-harness/historical/v1",
                identity.GetProperty("sourceIdentity").GetString(), Instant(manifest, "started_utc"),
                Instant(manifest, "finished_utc"), "succeeded", null, "")
        };
        JsonElement[] entries = [.. schedule.GetProperty("entries").EnumerateArray()];
        HashSet<int> seen = [];
        foreach ((string name, string kind) in new[] { ("timings.jsonl", "time"), ("counters.jsonl", "stat") })
        {
            string path = Path.Combine(directory, name);
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using SHA256 sha256 = SHA256.Create();
            await using CryptoStream hashed = new(input, sha256, CryptoStreamMode.Read, leaveOpen: true);
            using StreamReader reader = new(hashed, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Length != 0)
                    {
                        ImportRow(line, name, kind);
                    }
                }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException
                                              or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                // Preserve the digest diagnostic for damaged evidence. Rows already parsed
                // belong to the detached run and cannot reach the history transaction.
                await hashed.CopyToAsync(Stream.Null, ct);
                VerifyDigest(name, Convert.ToHexString(sha256.Hash!));
                throw;
            }

            VerifyDigest(name, Convert.ToHexString(sha256.Hash!));
        }

        foreach (string name in names)
        {
            run.Artifacts.Add(new(runId, null, "historical-" + name, Path.Combine(directory, name), hashes[name],
                name == "schedule.json" ? schedule.GetRawText() : null));
        }
        run.Artifacts.Add(new(runId, null, "historical-completion", markerPath, Hash(markerBytes), null));
        run.Run = run.Run with
        {
            SourceHash = Hash(Encoding.UTF8.GetBytes(string.Join("|",
                names.Order(StringComparer.Ordinal).Select(name => hashes[name]))))
        };
        return seen.Count != entries.Length ? throw new InvalidDataException("Historical completion has missing measurements.") : run;

        async Task<byte[]> ReadVerifiedAsync(string name)
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(directory, name), ct);
            VerifyDigest(name, Hash(bytes));
            return bytes;
        }

        void VerifyDigest(string name, string digest)
        {
            if (!string.Equals(digest, completion.GetProperty("sha256").GetProperty(name).GetString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Historical evidence changed after completion: " + name);
            }

            hashes.Add(name, digest);
        }

        void ImportRow(string line, string name, string kind)
        {
            ct.ThrowIfCancellationRequested();
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement row = document.RootElement;
            int order = row.GetProperty("schedule_order").GetInt32();
            if (order < 0 || order >= entries.Length || !seen.Add(order))
            {
                throw new InvalidDataException("Duplicate or invalid historical measurement order.");
            }
            JsonElement entry = entries[order];
            if (entry.GetProperty("order").GetInt32() != order || entry.GetProperty("kind").GetString() != kind ||
                entry.GetProperty("profile").GetString() != row.GetProperty("profile").GetString() ||
                entry.GetProperty("workers").GetInt32() != row.GetProperty("workers").GetInt32() ||
                entry.GetProperty("scenario").GetString() != row.GetProperty("scenario").GetString() ||
                entry.GetProperty("trial").GetInt32() != row.GetProperty("trial").GetInt32() ||
                entry.GetProperty("block").GetString() != row.GetProperty("block_id").GetString() ||
                entry.GetProperty("pair").GetString() != row.GetProperty("pair_id").GetString() ||
                identity.GetProperty("seed").GetUInt32() != row.GetProperty("seed").GetUInt32() ||
                identity.GetProperty("algorithm").GetString() != row.GetProperty("prng").GetString() ||
                schedule.GetProperty("prngVersion").GetString() != row.GetProperty("prng_version").GetString() ||
                identity.GetProperty("strategy").GetString() != row.GetProperty("schedule_strategy").GetString() ||
                identity.GetProperty("scope").GetString() != row.GetProperty("shuffle_scope").GetString() ||
                schedule.GetProperty("measurementSetSha256").GetString() != row.GetProperty("measurement_set_sha256").GetString())
            {
                throw new InvalidDataException("Historical measurement no longer matches its scheduled pair/block.");
            }
            string label = entry.GetProperty("label").GetString()!;
            // Variant is deliberately absent: both configurations in a pair share the same recipe.
            string workload = "{" + string.Join(",", WorkloadFields
                .Select(key => "\"" + key + "\":" + row.GetProperty(key).GetRawText())) + "}";
            run.Nodes.Add(new(runId, label, "historical-" + kind, "succeeded", null, null, null, null, row.GetProperty("profile").GetString()));
            if (kind == "time")
            {
                foreach (string metric in new[] { "time_us", "min_us", "max_us", "p99_us", "mtasks_s" })
                {
                    if (row.TryGetProperty(metric, out JsonElement value))
                    {
                        Add(metric, metric == "mtasks_s" ? "Mtasks/s" : "us", value);
                    }
                }
            }
            else
            {
                foreach (JsonProperty counter in row.GetProperty("counts").EnumerateObject())
                {
                    Add(counter.Name, "count", counter.Value);
                }
            }
            void Add(string metric, string unit, JsonElement value)
            {
                double number = value.GetDouble();
                if (!double.IsFinite(number)) { throw new InvalidDataException("Nonfinite historical measurement."); }
                run.Measurements.Add(new(runId, label, metric, unit, number, row.GetProperty("profile").GetString(),
                    row.GetProperty("scenario").GetString(), name, "historical-schedule/v1", row.GetProperty("trial").GetInt32(), workload, null));
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Instant(JsonElement manifest, string name) =>
        DateTimeOffset.Parse(manifest.GetProperty(name).GetString()!, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
