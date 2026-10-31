using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Host;
using Microsoft.Data.Sqlite;
using StreamJsonRpc;

internal static class ApplicationTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static async Task Refused(Func<Task> call, string code)
    {
        try { await call(); }
        catch (RemoteInvocationException error)
        {
            Check(error.ErrorCode == 1001, "Stable application error code missing: " + error);
            Check(error.DeserializedErrorData is ApplicationError data && data.Code == code,
                "Stable domain code missing: " + error.DeserializedErrorData);
            return;
        }
        throw new Exception("Expected refusal: " + code);
    }

    public static async Task RunAsync(string endpoint, string parent, CancellationToken ct)
    {
        var root = Directory.CreateDirectory(Path.Combine(parent, "application workspace")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(parent, "other application workspace")).FullName;
        var build = Directory.CreateDirectory(Path.Combine(root, "build space")).FullName;
        File.WriteAllText(Path.Combine(build, "CMakeCache.txt"), $"CMAKE_HOME_DIRECTORY:INTERNAL={root}\nCMAKE_GENERATOR:INTERNAL=Ninja\nCMAKE_BUILD_TYPE:STRING=Debug\n");
        var bridge = Directory.CreateDirectory(Path.Combine(build, "cmake-workflow", "targets", "Debug")).FullName;
        var executable = Path.Combine(build, "worker with spaces");
        File.WriteAllText(Path.Combine(bridge, "worker.json"),
            $$"""{"schemaVersion":1,"target":"worker","configuration":"Debug","executable":{{JsonSerializer.Serialize(executable)}}} """);
        var manifest = Path.Combine(root, "workflow.json");
        File.WriteAllText(manifest, """
            {"schemaVersion":2,"contexts":{"x":{"buildDirectory":"build space","mode":"adopt"}},
             "nodes":[{"id":"build","kind":"build","context":"x","target":"worker"},
                      {"id":"run","kind":"run","from":"build"}]}
            """);
        var run = Directory.CreateDirectory(Path.Combine(root, "run directory")).FullName;
        var report = new WorkflowReport { Workflow = manifest, Status = "failed", FinishedAt = DateTimeOffset.UtcNow,
            Nodes = [new() { Id = "run", Kind = "run", Status = "failed" }] };
        var raw = JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);
        File.WriteAllBytes(Path.Combine(run, "run.json"), raw);
        var otherRun = Directory.CreateDirectory(Path.Combine(outside, "run")).FullName;
        File.WriteAllBytes(Path.Combine(otherRun, "run.json"), raw);
        var database = Path.Combine(root, "history.sqlite");
        string ownId, otherId;
        using (var db = new HistoryStore(database))
        {
            db.Import(run, false); db.Import(otherRun, false);
            var runs = db.Runs(10, null, null, null, null, null);
            ownId = runs.Single(r => r.Directory == run).Id;
            otherId = runs.Single(r => r.Directory == otherRun).Id;
        }
        report.Status = "outcome_unknown"; report.Nodes[0].Status = "outcome_unknown";
        raw = JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);
        File.WriteAllBytes(Path.Combine(run, "run.json"), raw);
        var databaseBefore = File.ReadAllBytes(database);
        var cwd = Directory.GetCurrentDirectory();
        using var wire = await LocalTransport.ConnectAsync(endpoint, ct);
        using var rpc = HostConnection.Create(wire);
        var client = rpc.Attach<IHostRpc>(); rpc.StartListening();
        await Refused(() => client.RunGetAsync(new(run), ct), "not_initialized");
        await Refused(() => client.WorkflowStartAsync(
            new("preinit", manifest, "0", 1, Path.Combine(root, "preinit-results")), ct), "not_initialized");
        var info = await client.InitializeAsync(new(1, root), ct);
        Check(info.Capabilities.Contains("workflow/plan") && info.Capabilities.Contains("history/detect")
            && info.Capabilities.Contains("workflow/start"), "Capabilities omit implemented workflow/start lifecycle");
        // Recovery mutation and event replay must be real wire methods, not local-only helpers.
        var recoveryManifest = Path.Combine(root, "recovery-wire.json");
        var recoveryBytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        File.WriteAllBytes(recoveryManifest, recoveryBytes);
        string resumeId, blockedId;
        using (var durable = new DurableAcceptanceStore(root))
        {
            var hash = Convert.ToHexString(SHA256.HashData(recoveryBytes));
            resumeId = durable.Accept("wire-resume-admit", recoveryManifest, hash, 1, root).OperationId;
            blockedId = durable.Accept("wire-resume-block", recoveryManifest, hash, 1, root).OperationId;
            durable.AuthorizeDispatch(resumeId);
            durable.AuthorizeDispatch(blockedId);
            durable.Reconcile(resumeId, 0, "needs_attention", "manual repair");
            durable.Reconcile(blockedId, 0, "outcome_unknown", "unresolved process outcome");
        }
        var admitted = await client.WorkflowResumeAsync(new(resumeId, 1, true), ct);
        Check(admitted.Revision == 2 && admitted.Decision == "needs_attention", "resume wire CAS admission");
        Check((await client.WorkflowResumeAsync(new(resumeId, 2, true), ct)).Revision == 2,
            "resume wire idempotence");
        var emptyFeed = await client.OperationEventsAsync(new(resumeId, 0, 10), ct);
        Check(emptyFeed.Events.Count == 0 && !emptyFeed.HasMore, "event wire empty feed");
        try { await client.WorkflowResumeAsync(new(blockedId, 1, true), ct); throw new Exception("unknown resume allowed"); }
        catch (RemoteInvocationException) { }
        // Baseline after the mutation fixture (reconciliation + SQLite) is created.
        // Only subsequent read-only calls are forbidden from changing files.
        var filesBefore = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
        var context = await client.ContextInspectAsync(new(build), ct);
        Check(context.State == "ObservedLimited" && context.Configurations.SequenceEqual(["Debug"]), "Context wire result");
        var resolved = await client.TargetResolveAsync(new(build, "worker"), ct);
        Check(resolved.Executable == executable && resolved.Configuration == "Debug", "Target resolution changed paths/configuration");
        Check(!File.Exists(executable), "Read-only target resolution built an executable");
        var plan = await client.WorkflowPlanAsync(new(manifest), ct);
        var standalone = WorkflowQueries.PlanView(await WorkflowPlanner.LoadAsync(manifest, [], null, ct));
        Check(JsonSerializer.Serialize(plan, HostJson.Default.WorkflowPlanView)
            == JsonSerializer.Serialize(standalone, HostJson.Default.WorkflowPlanView), "CLI/Host plan semantics differ");
        var saved = await client.RunGetAsync(new(run), ct);
        Check(saved.Revision == Convert.ToHexString(SHA256.HashData(raw)) && saved.Report.Status == "outcome_unknown",
            "Report revision/status differs from authoritative bytes");
        Check(File.ReadAllBytes(Path.Combine(run, "run.json")).SequenceEqual(raw), "Report query performed recovery");
        var history = await client.HistoryRunsAsync(new(database, 1), ct);
        Check(history.Count == 1 && history[0].Id == ownId, "Workspace filtering must precede limit");
        var detail = await client.HistoryShowAsync(new(database, ownId), ct);
        Check(detail.Run.Id == ownId, "History detail wire result");
        await Refused(() => client.HistoryShowAsync(new(database, otherId), ct), "path_not_allowed");
        Check((await client.HistoryMetricsAsync(new(database, ownId), ct)).Count == 0, "Unexpected metrics");
        Check((await client.HistoryQueryAsync(new(database, Metric: "duration"), ct)).Count == 0, "Unexpected query result");
        var comparison = await client.HistoryCompareAsync(new(database, ownId, ownId), ct);
        Check(comparison.Single().Status == "missing", "History comparison invented measurements");
        var detection = await client.HistoryDetectAsync(new(database, ownId, ownId, "duration", "higher-worse", 5), ct);
        Check(detection.SchemaVersion == 1, "History detect wire result");
        await Refused(() => client.HistoryRunsAsync(new(database, 1001), ct), "invalid_argument");
        await Refused(() => client.HistoryRunsAsync(new(database, Since: "invalid"), ct), "invalid_argument");
        await Refused(() => client.HistoryRunsAsync(new(Path.Combine(root, "missing.sqlite")), ct), "not_found");
        await Refused(() => client.RunGetAsync(new(otherRun), ct), "path_not_allowed");
        await Refused(() => client.ContextInspectAsync(new("relative"), ct), "invalid_argument");
        Check(File.ReadAllBytes(database).SequenceEqual(databaseBefore) && !File.Exists(Path.Combine(root, "missing.sqlite")),
            "History query mutated or created a database");
        Check(Directory.GetCurrentDirectory() == cwd && filesBefore.SequenceEqual(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order()),
            "Read APIs created files or changed global cwd");

        // Invalid plans are data, not a transport success masquerading as valid.
        var invalid = Path.Combine(root, "invalid.json");
        File.WriteAllText(invalid, File.ReadAllText(manifest).Replace("\"adopt\"", "\"observe\""));
        Check(!(await client.WorkflowPlanAsync(new(invalid), ct)).Valid, "Known-invalid plan lost valid=false");
        if (!OperatingSystem.IsWindows())
        {
            var alias = Path.Combine(root, "escape");
            Directory.CreateSymbolicLink(alias, outside);
            await Refused(() => client.RunGetAsync(new(Path.Combine(alias, "run")), ct), "path_not_allowed");
            Directory.Delete(alias);
            // Checking just the build directory is insufficient: metadata can redirect reads.
            var cache = Path.Combine(build, "CMakeCache.txt");
            File.Move(cache, Path.Combine(outside, "cache.txt"));
            File.CreateSymbolicLink(cache, Path.Combine(outside, "cache.txt"));
            await Refused(() => client.ContextInspectAsync(new(build), ct), "path_not_allowed");
            File.Delete(cache); File.Move(Path.Combine(outside, "cache.txt"), cache);
        }
        // Read-only API must refuse old schema rather than silently migrate it.
        using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=3"; command.ExecuteNonQuery(); }
        var oldBytes = File.ReadAllBytes(database);
        await Refused(() => client.HistoryRunsAsync(new(database), ct), "invalid_data");
        Check(File.ReadAllBytes(database).SequenceEqual(oldBytes), "Read-only API migrated an old database");
        // Recovery fixtures already created the operations store. Lookup must not mutate it.
        var operationsDb = Path.Combine(root, ".cmakeworkflow", "operations.sqlite");
        var operationsDbBefore = File.ReadAllBytes(operationsDb);
        await Refused(() => client.OperationFindAsync(new("unknown"), ct), "not_found");
        Check(File.ReadAllBytes(operationsDb).SequenceEqual(operationsDbBefore),
            "Lookup modified the operations store");
        string operationId, acceptedWorkspaceId;
        using (var admissions = new DurableAcceptanceStore(root))
        {
            var manifestBytes = File.ReadAllBytes(manifest);
            var accepted = admissions.Accept("stable-client-id", manifest,
                Convert.ToHexString(SHA256.HashData(manifestBytes)), 1, root);
            operationId = accepted.OperationId;
            acceptedWorkspaceId = accepted.WorkspaceId;
        }
        var found = await client.OperationFindAsync(new("stable-client-id"), ct);
        var byId = await client.OperationGetAsync(new(operationId), ct);
        Check(found == byId && found.OperationId == operationId && found.WorkspaceId == acceptedWorkspaceId
            && found.State == "accepted_not_dispatched" && found.Kind == "workflow", "Operation read DTO mismatch");
        await Refused(() => client.OperationGetAsync(new("missing"), ct), "not_found");
        await Refused(() => client.OperationFindAsync(new(""), ct), "invalid_argument");
        using (var secondWire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (var secondRpc = HostConnection.Create(secondWire))
        {
            var second = secondRpc.Attach<IHostRpc>();
            secondRpc.StartListening();
            await second.InitializeAsync(new(1, outside), ct);
            await Refused(() => second.OperationGetAsync(new(operationId), ct), "not_found");
            await Refused(() => second.OperationFindAsync(new("stable-client-id"), ct), "not_found");
            Check(!Directory.Exists(Path.Combine(outside, ".cmakeworkflow")), "Foreign query created store");
        }
        Check(File.ReadAllBytes(Path.Combine(run, "run.json")).SequenceEqual(raw), "Operation lookup modified runner report");
        Console.WriteLine("Operation RPC wire checks passed: find/get, restart persistence, isolation and no dispatch.");
        Console.WriteLine("Read API wire checks passed: plan/context/report/history, revisions, isolation, symlink policy, no mutations and refusals.");
    }
}
