using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.History.Models;
using Microsoft.Data.Sqlite;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History.Storage;
#pragma warning restore IDE0130

public sealed class HistoryStore : IDisposable
{
    private const int CurrentIndexerVersion = 8;

    public static string DefaultDatabasePath =>
        Environment.GetEnvironmentVariable("CMAKEWORKFLOW_HISTORY_DB") is { Length: > 0 } value
            ? Path.GetFullPath(value)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CMakeWorkflow", "history.sqlite");

    private readonly SqliteConnection db;

    public HistoryStore(string path, bool readOnly = false)
    {
        SQLitePCL.Batteries.Init();
        path = Path.GetFullPath(path);
        if (!readOnly)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            db.Open();
            using (SqliteCommand foreignKeys = db.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys=ON";
                _ = foreignKeys.ExecuteNonQuery();
            }

            using SqliteCommand version = db.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            int current = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.CurrentCulture);
            if (readOnly)
            {
                if (current != 4)
                {
                    throw new InvalidDataException(
                        $"History schema {current} requires an explicit CLI migration; read-only API supports schema 4.");
                }

                return;
            }

            if (current > 4)
            {
                throw new InvalidDataException($"History database schema {current} is newer than supported version 4.");
            }

            if (current == 0)
            {
                Migrate0To1();
                current = 1;
            }

            if (current == 1)
            {
                Migrate1To2();
                current = 2;
            }

            if (current == 2)
            {
                Migrate2To3();
                current = 3;
            }

            if (current == 3)
            {
                Migrate3To4();
            }
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private void Migrate0To1()
    {
        using SqliteTransaction tx = db.BeginTransaction();
        Execute("""
                CREATE TABLE IF NOT EXISTS runs (
                  id TEXT PRIMARY KEY, directory TEXT NOT NULL UNIQUE, format TEXT NOT NULL,
                  workflow TEXT, manifest_hash TEXT, started_at TEXT, finished_at TEXT,
                  status TEXT NOT NULL, revision TEXT, source_hash TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS nodes (
                  run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                  id TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL,
                  duration_seconds REAL, dependencies TEXT, build_directory TEXT,
                  configuration TEXT, target TEXT, PRIMARY KEY(run_id,id));
                CREATE TABLE IF NOT EXISTS measurements (
                  run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                  node_id TEXT, metric TEXT NOT NULL, unit TEXT NOT NULL, value REAL NOT NULL,
                  variant TEXT, scenario TEXT, source TEXT NOT NULL, method TEXT NOT NULL,
                  sample_index INTEGER NOT NULL, workload TEXT, environment TEXT);
                CREATE TABLE IF NOT EXISTS artifacts (
                  run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                  node_id TEXT, type TEXT NOT NULL, path TEXT NOT NULL, sha256 TEXT, metadata TEXT);
                CREATE INDEX IF NOT EXISTS runs_status_time ON runs(status,started_at);
                CREATE INDEX IF NOT EXISTS measurements_metric ON measurements(metric,variant,scenario);
                """, tx);
        Execute("PRAGMA user_version=1", tx);
        tx.Commit();
    }

    private void Migrate1To2()
    {
        using SqliteTransaction tx = db.BeginTransaction();
        Execute("ALTER TABLE runs ADD COLUMN indexer_version INTEGER NOT NULL DEFAULT 0", tx);
        Execute("PRAGMA user_version=2", tx);
        tx.Commit();
    }

    private void Migrate2To3()
    {
        using SqliteTransaction tx = db.BeginTransaction();
        Execute("ALTER TABLE runs ADD COLUMN host_id TEXT", tx);
        Execute("ALTER TABLE runs ADD COLUMN environment_fingerprint TEXT", tx);
        Execute("ALTER TABLE runs ADD COLUMN environment_json TEXT", tx);
        Execute("PRAGMA user_version=3", tx);
        tx.Commit();
    }

    private void Migrate3To4()
    {
        using SqliteTransaction tx = db.BeginTransaction();
        Execute("ALTER TABLE runs ADD COLUMN build_environment_json TEXT", tx);
        Execute("PRAGMA user_version=4", tx);
        tx.Commit();
    }

    private void Execute(string sql, SqliteTransaction? tx = null, params object?[] args)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;

#pragma warning disable CA2100 // SQL structure comes only from trusted internal statements; external values are bound as SQLite parameters.
        cmd.CommandText = sql;
#pragma warning restore CA2100

        for (int i = 0; i < args.Length; i++)
        {
            _ = cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        }

        _ = cmd.ExecuteNonQuery();
    }

    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public string Import(string directory, bool recursive, Action<HistoryImportResult>? onComplete = null,
        Action<string>? pathGuard = null)
    {
        directory = Path.GetFullPath(directory);
        if (File.Exists(directory))
        {
            string file = Path.GetFileName(directory);
            if (file is not ("run.json" or "report.json" or "campaign-completion.json" or "historical-completion.json")
                && !file.EndsWith(".results.json", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsupported history input file: {directory}");
            }

            directory = Path.GetDirectoryName(directory)!;
        }

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Result directory is missing: {directory}");
        }

        IEnumerable<string> dirs = recursive ? CandidateDirectories(directory) : [directory];
        int imported = 0;
        int unchanged = 0;
        int skipped = 0;
        List<ParsedRun> parsedRuns = [];
        foreach (string path in dirs)
        {
            pathGuard?.Invoke(path);
            ParsedRun? parsed = HistoryReports.Parse(path);
            if (parsed is null)
            {
                skipped++;
                continue;
            }

            parsedRuns.Add(parsed);
        }

        List<ParsedRun> changed = [];
        foreach (ParsedRun parsed in parsedRuns)
        {
            (string? Hash, int IndexerVersion) = ExistingHash(parsed.Run.Id);
            if (Hash == parsed.Run.SourceHash && IndexerVersion == CurrentIndexerVersion
                                                  && !ProfileMetadataChanged(parsed))
            {
                unchanged++;
            }
            else
            {
                changed.Add(parsed);
            }
        }

        using SqliteTransaction tx = db.BeginTransaction();
        foreach (ParsedRun parsed in changed)
        {
            Execute(
                "DELETE FROM measurements WHERE run_id=$p0; DELETE FROM artifacts WHERE run_id=$p0; DELETE FROM nodes WHERE run_id=$p0; DELETE FROM runs WHERE id=$p0;",
                tx, parsed.Run.Id);
            HistoryRun r = parsed.Run;
            Execute(
                "INSERT INTO runs (id,directory,format,workflow,manifest_hash,started_at,finished_at,status,revision,source_hash,indexer_version,host_id,environment_fingerprint,environment_json,build_environment_json) VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13,$p14)",
                tx,
                r.Id, r.Directory, r.Format, r.Workflow, r.ManifestHash, r.StartedAt,
                r.FinishedAt, r.Status, r.Revision, r.SourceHash, CurrentIndexerVersion,
                r.HostId, r.EnvironmentFingerprint,
                r.Environment is null
                    ? null
                    : JsonSerializer.Serialize(r.Environment, HistoryJson.Default.HistoryHostEnvironment),
                r.BuildEnvironments is null
                    ? null
                    : JsonSerializer.Serialize(r.BuildEnvironments,
                        HistoryJson.Default.ListHistoryBuildEnvironment));
            foreach (HistoryNode n in parsed.Nodes)
            {
                Execute("INSERT INTO nodes VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)", tx,
                    n.RunId, n.Id, n.Kind, n.Status, n.DurationSeconds, n.Dependencies,
                    n.BuildDirectory, n.Configuration, n.Target);
            }

            foreach (HistoryMeasurement m in parsed.Measurements)
            {
                Execute("INSERT INTO measurements VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11)", tx,
                    m.RunId, m.NodeId, m.Metric, m.Unit, m.Value, m.Variant, m.Scenario,
                    m.Source, m.Method, m.SampleIndex, m.Workload, m.Environment);
            }

            foreach (HistoryArtifact? a in parsed.Artifacts.Distinct())
            {
                Execute("INSERT INTO artifacts VALUES ($p0,$p1,$p2,$p3,$p4,$p5)", tx,
                    a.RunId, a.NodeId, a.Type, a.Path, a.Sha256, a.Metadata);
            }

            imported++;
        }

        tx.Commit();
        onComplete?.Invoke(new(1, imported, unchanged, skipped));
        return $"Imported {imported}; unchanged {unchanged}; skipped {skipped}.";
    }

    /// <summary>Enumerates result roots while pruning beneath recognized reports and bulky artifact directories.</summary>
    /// <remarks>
    /// Traversal order is deterministic for child names. Once a directory has a supported report/capture, its
    /// children are not separate import candidates. This enumeration does not authorize paths, prevent link
    /// cycles or freeze files; the import caller supplies any path guard and handles parse failures before commit.
    /// </remarks>
    private static IEnumerable<string> CandidateDirectories(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string path = pending.Pop();
            yield return path;
            if (File.Exists(Path.Combine(path, "run.json"))
                || File.Exists(Path.Combine(path, "campaign-completion.json"))
                || File.Exists(Path.Combine(path, "report.json"))
                || File.Exists(Path.Combine(path, "historical-completion.json"))
                || Directory.GetFiles(path, "*.results.json", SearchOption.TopDirectoryOnly).Length > 0)
            {
                continue;
            }

            foreach (string? child in Directory.EnumerateDirectories(path)
                         .Where(x => Path.GetFileName(x) is not ("corpus" or "traces" or "raw" or "artifacts"))
                         .OrderByDescending(x => x, StringComparer.Ordinal))
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>Tests the supplemental artifact-count invalidation condition for an otherwise matching import.</summary>
    /// <remarks>
    /// Compares persisted artifact count with distinct parsed entries. It does not compare every artifact
    /// value and must be combined with SourceHash and indexer-version checks in Import. Uses the store-owned
    /// SQLite connection; callers serialize access to that connection.
    /// </remarks>
    private bool ProfileMetadataChanged(ParsedRun run)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM artifacts WHERE run_id=$p0";
        _ = cmd.Parameters.AddWithValue("$p0", run.Run.Id);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.CurrentCulture) != run.Artifacts.Distinct().Count();
    }

    private (string? Hash, int IndexerVersion) ExistingHash(string id)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT source_hash,indexer_version FROM runs WHERE id=$p0";
        _ = cmd.Parameters.AddWithValue("$p0", id);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt32(1)) : (null, 0);
    }

    private static HistoryRun Run(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), S(r, 3), S(r, 4),
        S(r, 5), S(r, 6), r.GetString(7), S(r, 8), r.GetString(9),
        Directory.Exists(r.GetString(1)), S(r, 10), S(r, 11), S(r, 12) is { } json
            ? JsonSerializer.Deserialize(json, HistoryJson.Default.HistoryHostEnvironment)
            : null,
        S(r, 13) is { } builds
            ? JsonSerializer.Deserialize(builds,
                HistoryJson.Default.ListHistoryBuildEnvironment)
            : null);

    [SuppressMessage("Design", "CA1002", Justification = "Materializes SQLite rows into an owned List for History result DTOs and Host response construction; the reader is disposed before the list escapes.")]
    public List<HistoryRun> Runs(int limit, string? status, string? since, string? until,
        string? workflow, string? variant, Func<string, bool>? includeDirectory = null, CancellationToken ct = default)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
                          SELECT id,directory,format,workflow,manifest_hash,started_at,finished_at,status,revision,source_hash,host_id,environment_fingerprint,environment_json,build_environment_json
                          FROM runs WHERE ($p0 IS NULL OR status=$p0)
                            AND ($p1 IS NULL OR started_at >= $p1)
                            AND ($p2 IS NULL OR started_at <= $p2)
                            AND ($p3 IS NULL OR workflow LIKE '%' || $p3 || '%')
                            AND ($p4 IS NULL OR EXISTS(SELECT 1 FROM measurements m WHERE m.run_id=runs.id AND m.variant=$p4))
                          ORDER BY started_at DESC, id LIMIT $p5
                          """;
        object?[] values = [status, since, until, workflow, variant, includeDirectory is null ? limit : int.MaxValue];
        for (int i = 0; i < values.Length; i++)
        {
            _ = cmd.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        }

        using SqliteDataReader reader = cmd.ExecuteReader();
        List<HistoryRun> result = [];
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (includeDirectory is not null && !includeDirectory(reader.GetString(1)))
            {
                continue;
            }

            result.Add(Run(reader));
            if (result.Count >= limit)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Stable keyset traversal of imported runs, without modifying the history database.</summary>
    /// <remarks>New imports are excluded by the first page's rowid watermark. Deletions or
    /// updates to existing run rows are not snapshot-isolated across requests.</remarks>
    public HistoryRunPage RunsPage(string databaseIdentity, int limit, string? status,
        string? since, string? until, string? workflow, string? variant, string? cursor,
        Func<string, bool>? includeDirectory = null, CancellationToken ct = default)
    {
        if (limit is < 1 or > 200 || string.IsNullOrWhiteSpace(databaseIdentity) ||
            cursor is { Length: < 1 or > 2048 })
        {
            throw new ArgumentException("Invalid history page request.");
        }

        ct.ThrowIfCancellationRequested();
        string binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0",
            databaseIdentity, status ?? "", since ?? "", until ?? "", workflow ?? "", variant ?? ""))));
        long watermark;
        string? lastStarted = null;
        string? lastId = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
                {
                    throw new FormatException();
                }

                string input = cursor.Replace('-', '+').Replace('_', '/');
                byte[] encoded = Convert.FromBase64String(input + new string('=', (4 - (input.Length % 4)) % 4));
                string[] fields = Encoding.UTF8.GetString(encoded).Split('\n');
                if (fields.Length != 5 || fields[0] != "v1" || fields[1] != binding ||
                    !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out watermark) ||
                    watermark <= 0 || fields[4].Length is < 1 or > 256)
                {
                    throw new FormatException();
                }

                lastStarted = fields[3] == "-" ? null : Encoding.UTF8.GetString(
                    Convert.FromBase64String(fields[3] + new string('=', (4 - (fields[3].Length % 4)) % 4)));
                lastId = fields[4];
            }
            catch (Exception error) when (error is FormatException or ArgumentException or DecoderFallbackException)
            {
                throw new ArgumentException("Invalid or mismatched history page cursor.", nameof(cursor), error);
            }
        }
        else
        {
            using SqliteCommand max = db.CreateCommand();
            max.CommandText = "SELECT COALESCE(MAX(rowid),0) FROM runs;";
            watermark = (long)max.ExecuteScalar()!;
        }
        if (watermark == 0)
        {
            return new(1, [], null);
        }

        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,directory,format,workflow,manifest_hash,started_at,finished_at,status,revision,source_hash,host_id,environment_fingerprint,environment_json,build_environment_json
            FROM runs
            WHERE rowid <= $watermark
              AND ($status IS NULL OR status = $status)
              AND ($since IS NULL OR started_at >= $since)
              AND ($until IS NULL OR started_at <= $until)
              AND ($workflow IS NULL OR workflow LIKE '%' || $workflow || '%')
              AND ($variant IS NULL OR EXISTS (SELECT 1 FROM measurements m WHERE m.run_id = runs.id AND m.variant = $variant))
              AND ($lastId IS NULL OR
                   ($lastStarted IS NULL AND started_at IS NULL AND id > $lastId) OR
                   ($lastStarted IS NOT NULL AND
                    (started_at < $lastStarted OR started_at IS NULL OR
                     (started_at = $lastStarted AND id > $lastId))))
            ORDER BY started_at DESC, id ASC;
            """;
        _ = cmd.Parameters.AddWithValue("$watermark", watermark);
        _ = cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("$since", (object?)since ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("$until", (object?)until ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("$workflow", (object?)workflow ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("$variant", (object?)variant ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("$lastId", (object?)lastId ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("$lastStarted", (object?)lastStarted ?? DBNull.Value);

        List<HistoryRun> items = new(limit);
        string? continuationId = null;
        string? continuationStart = null;
        bool hasMore = false;
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (includeDirectory is not null && !includeDirectory(reader.GetString(1)))
                {
                    continue;
                }

                if (items.Count == limit) { hasMore = true; break; }
                items.Add(Run(reader));
                continuationId = reader.GetString(0);
                continuationStart = reader.IsDBNull(5) ? null : reader.GetString(5);
            }
        }
        if (!hasMore)
        {
            return new(1, items, null);
        }

        string startedToken = continuationStart is null ? "-" : Convert.ToBase64String(
            Encoding.UTF8.GetBytes(continuationStart)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string value = string.Join("\n", "v1", binding,
            watermark.ToString(CultureInfo.InvariantCulture), startedToken, continuationId);
        string next = Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
        return new(1, items, next);
    }

    public HistoryDetail Show(string id, Action<string>? validateDirectory = null,
        int maxMeasurements = int.MaxValue, Func<string, bool>? artifactAllowed = null, CancellationToken ct = default)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT id,directory,format,workflow,manifest_hash,started_at,finished_at,status,revision,source_hash,host_id,environment_fingerprint,environment_json,build_environment_json FROM runs WHERE id=$p0";
        _ = cmd.Parameters.AddWithValue("$p0", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read())
        {
            throw new KeyNotFoundException($"Unknown run ID: {id}");
        }

        validateDirectory?.Invoke(r.GetString(1));
        HistoryRun run = Run(r);
        return new(run, Nodes(id), Metrics(id, null, null, null, maxRows: maxMeasurements, ct: ct),
            Artifacts(id, artifactAllowed));
    }

    private List<HistoryNode> Nodes(string id)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT run_id,id,kind,status,duration_seconds,dependencies,build_directory,configuration,target FROM nodes WHERE run_id=$p0 ORDER BY id";
        _ = cmd.Parameters.AddWithValue("$p0", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<HistoryNode> list = [];
        while (r.Read())
        {
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetString(3), D(r, 4), S(r, 5), S(r, 6), S(r, 7), S(r, 8)));
        }

        return list;
    }

    [SuppressMessage("Design", "CA1002", Justification = "Materializes SQLite rows into an owned List for History result DTOs and Host response construction; the reader is disposed before the list escapes.")]
    public List<HistoryMeasurement> Metrics(string? run, string? metric, string? variant, string? scenario,
        Func<string, bool>? includeDirectory = null, int maxRows = int.MaxValue, CancellationToken ct = default)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
                          SELECT run_id,node_id,metric,unit,value,variant,scenario,source,method,sample_index,workload,environment,
                            (SELECT directory FROM runs WHERE id=measurements.run_id)
                          FROM measurements WHERE ($p0 IS NULL OR run_id=$p0) AND ($p1 IS NULL OR metric=$p1)
                            AND ($p2 IS NULL OR variant=$p2) AND ($p3 IS NULL OR scenario=$p3)
                          ORDER BY metric,unit,method,variant,scenario,run_id,sample_index,node_id
                          """;
        object?[] values = [run, metric, variant, scenario];
        for (int i = 0; i < values.Length; i++)
        {
            _ = cmd.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        }

        using SqliteDataReader r = cmd.ExecuteReader();
        List<HistoryMeasurement> list = [];
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (includeDirectory is not null && !includeDirectory(r.GetString(12)))
            {
                continue;
            }

            if (list.Count >= maxRows)
            {
                throw new InvalidDataException($"Measurement limit {maxRows} exceeded; narrow the query.");
            }

            list.Add(new(r.GetString(0), S(r, 1), r.GetString(2), r.GetString(3),
                r.GetDouble(4), S(r, 5), S(r, 6), r.GetString(7), r.GetString(8), r.GetInt32(9),
                S(r, 10), S(r, 11)));
        }

        return list;
    }

    private List<HistoryArtifact> Artifacts(string id, Func<string, bool>? allowed = null)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT run_id,node_id,type,path,sha256,metadata FROM artifacts WHERE run_id=$p0 ORDER BY type,path";
        _ = cmd.Parameters.AddWithValue("$p0", id);
        using SqliteDataReader r = cmd.ExecuteReader();
        List<HistoryArtifact> list = [];
        while (r.Read())
        {
            list.Add(new(r.GetString(0), S(r, 1), r.GetString(2), r.GetString(3),
                S(r, 4), S(r, 5),
                (allowed is null || allowed(r.GetString(3))) &&
                (File.Exists(r.GetString(3)) || Directory.Exists(r.GetString(3)))));
        }

        return list;
    }

    public void Dispose() => db.Dispose();
}