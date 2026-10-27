using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.History;

public sealed class HistoryStore : IDisposable
{
    private const int CurrentIndexerVersion = 2;
    private readonly SqliteConnection db;
    public HistoryStore(string path)
    {
        SQLitePCL.Batteries.Init();
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        db.Open();
        using (var foreignKeys = db.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys=ON";
            foreignKeys.ExecuteNonQuery();
        }
        using var version = db.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current > 2) throw new InvalidDataException($"History database schema {current} is newer than supported version 2.");
        if (current == 0) { Migrate0To1(); current = 1; }
        if (current == 1) Migrate1To2();
    }
    private void Migrate0To1()
    {
        using var tx = db.BeginTransaction();
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
        using var tx = db.BeginTransaction();
        Execute("ALTER TABLE runs ADD COLUMN indexer_version INTEGER NOT NULL DEFAULT 0", tx);
        Execute("PRAGMA user_version=2", tx);
        tx.Commit();
    }
    private void Execute(string sql, SqliteTransaction? tx = null, params object?[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    private static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    public string Import(string directory, bool recursive)
    {
        directory = Path.GetFullPath(directory);
        if (File.Exists(directory))
        {
            var file = Path.GetFileName(directory);
            if (file is not ("run.json" or "report.json" or "campaign-completion.json")
                && !file.EndsWith(".results.json", StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported history input file: {directory}");
            directory = Path.GetDirectoryName(directory)!;
        }
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Result directory is missing: {directory}");
        var dirs = recursive ? CandidateDirectories(directory) : [directory];
        var imported = 0; var unchanged = 0; var skipped = 0;
        var parsedRuns = new List<ParsedRun>();
        foreach (var path in dirs)
        {
            var parsed = HistoryReports.Parse(path);
            if (parsed is null) { skipped++; continue; }
            parsedRuns.Add(parsed);
        }
        var changed = new List<ParsedRun>();
        foreach (var parsed in parsedRuns)
        {
            var old = ExistingHash(parsed.Run.Id);
            if (old.Hash == parsed.Run.SourceHash && old.IndexerVersion == CurrentIndexerVersion
                && !ProfileMetadataChanged(parsed))
                unchanged++;
            else changed.Add(parsed);
        }
        using var tx = db.BeginTransaction();
        foreach (var parsed in changed)
        {
            Execute("DELETE FROM measurements WHERE run_id=$p0; DELETE FROM artifacts WHERE run_id=$p0; DELETE FROM nodes WHERE run_id=$p0; DELETE FROM runs WHERE id=$p0;",
                tx, parsed.Run.Id);
            var r = parsed.Run;
            Execute("INSERT INTO runs VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10)", tx,
                r.Id, r.Directory, r.Format, r.Workflow, r.ManifestHash, r.StartedAt,
                r.FinishedAt, r.Status, r.Revision, r.SourceHash, CurrentIndexerVersion);
            foreach (var n in parsed.Nodes)
                Execute("INSERT INTO nodes VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8)", tx,
                    n.RunId, n.Id, n.Kind, n.Status, n.DurationSeconds, n.Dependencies,
                    n.BuildDirectory, n.Configuration, n.Target);
            foreach (var m in parsed.Measurements)
                Execute("INSERT INTO measurements VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11)", tx,
                    m.RunId, m.NodeId, m.Metric, m.Unit, m.Value, m.Variant, m.Scenario,
                    m.Source, m.Method, m.SampleIndex, m.Workload, m.Environment);
            foreach (var a in parsed.Artifacts.Distinct())
                Execute("INSERT INTO artifacts VALUES ($p0,$p1,$p2,$p3,$p4,$p5)", tx,
                    a.RunId, a.NodeId, a.Type, a.Path, a.Sha256, a.Metadata);
            imported++;
        }
        tx.Commit();
        return $"Imported {imported}; unchanged {unchanged}; skipped {skipped}.";
    }
    private static IEnumerable<string> CandidateDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var path = pending.Pop();
            yield return path;
            if (File.Exists(Path.Combine(path, "run.json"))
                || File.Exists(Path.Combine(path, "campaign-completion.json"))
                || File.Exists(Path.Combine(path, "report.json"))
                || Directory.GetFiles(path, "*.results.json", SearchOption.TopDirectoryOnly).Length > 0)
                continue;
            foreach (var child in Directory.EnumerateDirectories(path)
                .Where(x => Path.GetFileName(x) is not ("corpus" or "traces" or "raw" or "artifacts"))
                .OrderByDescending(x => x, StringComparer.Ordinal))
                pending.Push(child);
        }
    }
    private bool ProfileMetadataChanged(ParsedRun run)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM artifacts WHERE run_id=$p0";
        cmd.Parameters.AddWithValue("$p0", run.Run.Id);
        return Convert.ToInt32(cmd.ExecuteScalar()) != run.Artifacts.Distinct().Count();
    }
    private (string? Hash, int IndexerVersion) ExistingHash(string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT source_hash,indexer_version FROM runs WHERE id=$p0";
        cmd.Parameters.AddWithValue("$p0", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt32(1)) : (null, 0);
    }
    private static HistoryRun Run(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), S(r, 3), S(r, 4),
        S(r, 5), S(r, 6), r.GetString(7), S(r, 8), r.GetString(9),
        Directory.Exists(r.GetString(1)));
    public List<HistoryRun> Runs(int limit, string? status, string? since, string? until,
        string? workflow, string? variant)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,directory,format,workflow,manifest_hash,started_at,finished_at,status,revision,source_hash
            FROM runs WHERE ($p0 IS NULL OR status=$p0)
              AND ($p1 IS NULL OR started_at >= $p1)
              AND ($p2 IS NULL OR started_at <= $p2)
              AND ($p3 IS NULL OR workflow LIKE '%' || $p3 || '%')
              AND ($p4 IS NULL OR EXISTS(SELECT 1 FROM measurements m WHERE m.run_id=runs.id AND m.variant=$p4))
            ORDER BY started_at DESC, id LIMIT $p5
            """;
        object?[] values = [status, since, until, workflow, variant, limit];
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var result = new List<HistoryRun>();
        while (reader.Read()) result.Add(Run(reader));
        return result;
    }
    public HistoryDetail Show(string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,directory,format,workflow,manifest_hash,started_at,finished_at,status,revision,source_hash FROM runs WHERE id=$p0";
        cmd.Parameters.AddWithValue("$p0", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException($"Unknown run ID: {id}");
        var run = Run(r);
        return new(run, Nodes(id), Metrics(id, null, null, null), Artifacts(id));
    }
    private List<HistoryNode> Nodes(string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT run_id,id,kind,status,duration_seconds,dependencies,build_directory,configuration,target FROM nodes WHERE run_id=$p0 ORDER BY id";
        cmd.Parameters.AddWithValue("$p0", id);
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryNode>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2),
            r.GetString(3), D(r, 4), S(r, 5), S(r, 6), S(r, 7), S(r, 8)));
        return list;
    }
    public List<HistoryMeasurement> Metrics(string? run, string? metric, string? variant, string? scenario)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT run_id,node_id,metric,unit,value,variant,scenario,source,method,sample_index,workload,environment
            FROM measurements WHERE ($p0 IS NULL OR run_id=$p0) AND ($p1 IS NULL OR metric=$p1)
              AND ($p2 IS NULL OR variant=$p2) AND ($p3 IS NULL OR scenario=$p3)
            ORDER BY metric,unit,method,variant,scenario,run_id,sample_index,node_id
            """;
        object?[] values = [run, metric, variant, scenario];
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$p" + i, values[i] ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryMeasurement>();
        while (r.Read()) list.Add(new(r.GetString(0), S(r, 1), r.GetString(2), r.GetString(3),
            r.GetDouble(4), S(r, 5), S(r, 6), r.GetString(7), r.GetString(8), r.GetInt32(9),
            S(r, 10), S(r, 11)));
        return list;
    }
    private List<HistoryArtifact> Artifacts(string id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT run_id,node_id,type,path,sha256,metadata FROM artifacts WHERE run_id=$p0 ORDER BY type,path";
        cmd.Parameters.AddWithValue("$p0", id);
        using var r = cmd.ExecuteReader();
        var list = new List<HistoryArtifact>();
        while (r.Read()) list.Add(new(r.GetString(0), S(r, 1), r.GetString(2), r.GetString(3),
            S(r, 4), S(r, 5), File.Exists(r.GetString(3)) || Directory.Exists(r.GetString(3))));
        return list;
    }
    public void Dispose() => db.Dispose();
}
