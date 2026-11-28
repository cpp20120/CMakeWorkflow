using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CMakeWorkflow.Core.Processes;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.Host.Execution;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Transport;
using CMakeWorkflow.Host.Workflows;
using Microsoft.Data.Sqlite;
using StreamJsonRpc;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static class ProcessRecoveryTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok)
        {
            throw new UnreachableException(message);
        }
    }

    private static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or
                                      ApplicationRefusal or RemoteInvocationException or SqliteException or JsonException)
        { return; }
        throw new UnreachableException("Accepted unsafe recovery: " + message);
    }

    private sealed record Fixture(string Root, string Manifest, string Output, string Id,
        DurableImmutableRequest Request, string? EffectFile = null, bool Native = false)
    {
        internal string Report => Path.Combine(Output, "run.json");
        internal string Effects => EffectFile ?? Path.Combine(Root, "effects.txt");
        internal DurableAcceptanceStore Store() => new(Root, false, ProcessCheckpointEvidenceCodec.Instance);
        internal ProcessOperationReport Read()
        {
            using DurableAcceptanceStore store = Store();
            return Native
                ? store.ProcessEvidenceCodec.Read(store.ReadProcessCheckpoint(Id, Output))
                : ProcessCheckpointEvidenceCodec.Parse(File.ReadAllBytes(Report));
        }
        internal void Write(ProcessOperationReport report) => File.WriteAllBytes(Report,
            JsonSerializer.SerializeToUtf8Bytes(report, ProcessWorkflowJson.Default.ProcessOperationReport));
    }

    private static Fixture Create(string parent, bool slow = false, bool native = false)
    {
        string root = Path.Combine(parent, "process-recovery-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        string effects = Path.Combine(root, "effects.txt");
        ProcessWorkflowNode Node(string id, List<string>? deps = null) => new(id, "dotnet",
        [typeof(ProcessRecoveryTests).Assembly.Location, "--process-recovery-child", effects, id,
            slow && id == "b" ? "slow" : "fast"], deps);
        ProcessWorkflowManifest manifest = new(1, "process", [Node("a"), Node("b", ["a"])]);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ProcessWorkflowJson.Default.ProcessWorkflowManifest);
        string path = Path.Combine(root, "process.json");
        File.WriteAllBytes(path, bytes);
        string output = Path.Combine(root, "run");
        DurableImmutableRequest request = ProcessWorkflowDomain.Admission(new("process", Guid.NewGuid().ToString("N"), path,
            ProcessWorkflowRecovery.Hash(bytes), 1, output));
        if (native)
        {
            BinaryWorkflowSubmission.InitializeWorkspace(root);
        }

        using DurableAcceptanceStore store = new(root, false, ProcessCheckpointEvidenceCodec.Instance);
        return new(root, path, output, store.AcceptImmutable(request).OperationId, request, Native: native);
    }

    // Real child process. Tests kill the executor at deterministic fault markers;
    // the child effect file is independent of reports and SQLite.
    internal static async Task ChildAsync(string[] args)
    {
        await File.AppendAllTextAsync(args[0], args[1] + "\n");
        Console.WriteLine("retained output " + args[1]);
        if (args[2] == "slow")
        {
            await File.WriteAllTextAsync(args[0] + ".alive", args[1]);
            await Task.Delay(TimeSpan.FromSeconds(60));
        }
    }

    internal static async Task WorkerAsync(string[] args)
    {
        string root = args[0];
        string id = args[1];
        if (args[2] == "resume")
        {
            _ = await DurableProcessWorkflowExecutor.ExecuteResumedAsync(root, id, default);
        }
        else
        {
            using DurableAcceptanceStore store = new(root, false, ProcessCheckpointEvidenceCodec.Instance);
            AcceptedWorkflow accepted = store.Get(id)!;
            string manifest = args[3];
            DurableImmutableRequest request = ProcessWorkflowDomain.Admission(new("process", accepted.ClientRequestId, manifest,
                accepted.ManifestSha256, 1, args[4]));
            _ = await DurableProcessWorkflowExecutor.ExecuteAcceptedAsync(root, id, request, 1, default);
        }
    }

    private static Process Start(Fixture f, bool resume, string point, string? node, string signal)
    {
        ProcessStartInfo info = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string? arg in new[] { typeof(ProcessRecoveryTests).Assembly.Location, "--process-recovery-worker",
                     f.Root, f.Id, resume ? "resume" : "start", f.Manifest, f.Output })
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["CMAKEWORKFLOW_TEST_FAULT"] = point;
        if (node is not null)
        {
            info.Environment["CMAKEWORKFLOW_TEST_NODE"] = node;
        }
        else
        {
            _ = info.Environment.Remove("CMAKEWORKFLOW_TEST_NODE");
        }

        info.Environment["CMAKEWORKFLOW_TEST_MARKER"] = signal;
        return Process.Start(info) ?? throw new UnreachableException("Cannot start process fault worker.");
    }

    private static async Task Crash(Fixture f, bool resume, string point, string? node = null, bool effect = false, bool killTree = true)
    {
        string signal = Path.Combine(f.Root, "barrier-" + Guid.NewGuid().ToString("N"));
        using Process child = Start(f, resume, point, node, signal);
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            while ((effect ? !File.Exists(f.Effects) || !(await File.ReadAllLinesAsync(f.Effects, CancellationToken.None)).Contains("b") : !File.Exists(signal)) ||
                   (!killTree && !File.Exists(f.Effects + ".alive")))
            {
                if (child.HasExited)
                {
                    throw new UnreachableException(
                        $"Fault worker exited before reaching '{point}' (exit code {child.ExitCode}). " +
                        "Rebuild with -p:RecoveryFaultInjection=true to enable test barriers. " +
                        "stderr: " + await stderr + " stdout: " + await stdout);
                }

                await Task.Delay(20, deadline.Token);
            }
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: killTree);
            }

            await child.WaitForExitAsync();
            _ = await Task.WhenAll(stderr, stdout);
        }
    }

    private static async Task<Fixture> Partial(string parent, bool slow = false, bool native = false)
    {
        Fixture f = Create(parent, slow, native);
        await Crash(f, false, "process-before-reserve", "b");
        Check((await File.ReadAllLinesAsync(f.Effects, CancellationToken.None)).SequenceEqual(["a"]), "Partial run duplicated A.");
        Check(f.Read().Nodes.Select(n => n.Status).SequenceEqual(["succeeded", "pending"]),
            "Partial fixture is not a checkpointed prerequisite and never-admitted child.");
        return f;
    }

    private static async Task AdmitAsync(Fixture f)
    {
        using DurableAcceptanceStore store = f.Store();
        _ = store.Reconcile(f.Id, 0, "needs_attention", "Verified quiescence; continue only never-admitted nodes");
        await ProcessWorkflowRecovery.PrepareAsync(store, f.Id, new(f.Root), CancellationToken.None);
        _ = store.AdmitResume(f.Id, 1);
    }

    private static async Task VerifyInterruptedNativeProcessAsync(Fixture fixture, string point)
    {
        using DurableAcceptanceStore store = fixture.Store();
        Check(store.GetFinalization(fixture.Id) is null, "Interrupted native execution was finalized.");
        _ = store.Reconcile(fixture.Id, 0, "needs_attention", "Outcome remains uncertain");
        await Reject(() => ProcessWorkflowRecovery.PrepareAsync(store, fixture.Id, new(fixture.Root), default), point);
    }

    private static void VerifyTerminalBoundary(Fixture fixture, bool committed)
    {
        using DurableAcceptanceStore store = fixture.Store();
        Check((store.GetFinalization(fixture.Id) is { State: "succeeded" }) == committed,
            "Terminal visibility crossed the SQLite commit boundary.");
        Check(File.ReadAllLines(fixture.Effects).SequenceEqual(["a", "b"]),
            "Terminal evidence fault repeated work.");
    }

    private static async Task VerifySurvivingChildAsync(string parent)
    {
        Fixture fixture = Create(parent, slow: true, native: true);
        await Crash(fixture, false, "process-after-identity-observation", "b", killTree: false);
        (int pid, string token) = await ReadDurableBirthTokenAsync(fixture);
        using Process child = Process.GetProcessById(pid);
        Check(WorkflowProcessBackend.ReadBirthToken(child) == token, "PID alone cannot identify the surviving child.");
        try
        {
            Check(await DurableProcessWorkflowExecutor.ExecuteAcceptedAsync(fixture.Root, fixture.Id,
                fixture.Request, 1, default) == 4, "A surviving process allowed a new dispatch.");
            Check(!child.HasExited, "Retry signaled the surviving child.");
            await VerifyInterruptedNativeProcessAsync(fixture, "surviving child");
        }
        finally
        {
            if (!child.HasExited && WorkflowProcessBackend.ReadBirthToken(child) == token)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<(int Pid, string Token)> ReadDurableBirthTokenAsync(Fixture fixture)
    {
        using SqliteConnection ledger = new(new SqliteConnectionStringBuilder
        {
            DataSource = WorkflowLedgerPath.Resolve(fixture.Root),
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await ledger.OpenAsync();
        using SqliteCommand identity = ledger.CreateCommand();
        identity.CommandText = """
            SELECT lifecycle.pid, birth.token FROM process_lifecycle AS lifecycle
            JOIN process_birth_tokens AS birth USING(operation_id,node_id,attempt,stage)
            WHERE lifecycle.node_id='b' AND lifecycle.stage=2;
            """;
        using SqliteDataReader row = await identity.ExecuteReaderAsync();
        Check(await row.ReadAsync(), "A started process has no durable identity.");
        return (row.GetInt32(0), row.GetString(1));
    }

    internal static async Task MeasureNativeAndLegacyAsync(string parent)
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(parent), "process-measure-" +
            Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (bool native in new[] { false, true })
            {
                Fixture fixture = Create(root, native: native);
                _ = Directory.CreateDirectory(fixture.Output);
                using DurableAcceptanceStore store = fixture.Store();
                store.ReserveResultsDirectory(fixture.Id, fixture.Output);
                _ = store.AuthorizeDispatch(fixture.Id);
                ProcessOperationReport report = new()
                {
                    SnapshotSha256 = fixture.Request.SnapshotSha256,
                    Nodes =
                    [
                        new() { Id = "a", Dependencies = [] },
                        new() { Id = "b", Dependencies = ["a"] }
                    ]
                };
                List<double> checkpointUs = [], reopenUs = [], recoverMs = [];
                long bytes = 0;
                WorkspaceReadPaths paths = new(fixture.Root);
                for (int sample = 0; sample < 23; sample++)
                {
                    long began = Stopwatch.GetTimestamp();
                    if (native)
                    {
                        bytes = store.SaveProcessCheckpoint(fixture.Id, report).Length;
                    }
                    else
                    {
                        byte[] json = DurableAcceptanceStore.ProjectProcessReport(report);
                        await DurableByteWriter.WriteAsync(fixture.Report, json, CancellationToken.None);
                        bytes = json.Length;
                    }

                    double checkpoint = Stopwatch.GetElapsedTime(began).TotalMicroseconds;
                    began = Stopwatch.GetTimestamp();
                    _ = store.ProcessEvidenceCodec.Read(await store.ReadProcessEvidenceAsync(paths, fixture.Id,
                        fixture.Output, CancellationToken.None));
                    if (sample >= 2)
                    {
                        checkpointUs.Add(checkpoint);
                        reopenUs.Add(Stopwatch.GetElapsedTime(began).TotalMicroseconds);
                    }
                }

                for (int sample = 0; sample < 7; sample++)
                {
                    Fixture partial = await Partial(root, native: native);
                    long began = Stopwatch.GetTimestamp();
                    await AdmitAsync(partial);
                    Check(await DurableProcessWorkflowExecutor.ExecuteResumedAsync(partial.Root, partial.Id, default) == 0,
                        "Process persistence measurement did not recover.");
                    recoverMs.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                }

                Console.WriteLine(FormattableString.Invariant(
                    $"{(native ? "native" : "legacy")} checkpoint_bytes={bytes} checkpoint_write_us={checkpointUs.Order().ElementAt(10):F3} checkpoint_read_us={reopenUs.Order().ElementAt(10):F3} recovery_with_child_ms={recoverMs.Order().ElementAt(3):F3}"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task RunNativeAsync(string parent, string capnp, string schema)
    {
        Fixture f = await Partial(parent, native: true);
        WorkspaceReadPaths paths = new(f.Root);
        OperationEventsPage prior = await OperationEventReplay.ReadAsync(paths, new(f.Id, 0, 20), default);
        Check(prior.Events.Select(item => item.Sequence).SequenceEqual([1L, 2L, 3L]),
            "Committed native process history diverged from the checkpoint.");
        await AdmitAsync(f);
        await Crash(f, true, "process-before-resume-claim");
        File.Delete(f.Manifest);
        bool[] claims = await Task.WhenAll(Task.Run(AttemptResumeAsync), Task.Run(AttemptResumeAsync));
        Check(claims.Count(claimed => claimed) == 1, "Concurrent native resume attempts did not establish one owner.");

        async Task<bool> AttemptResumeAsync()
        {
            try
            {
                return await DurableProcessWorkflowExecutor.ExecuteResumedAsync(f.Root, f.Id, default) == 0;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                return false;
            }
        }

        await Reject(() => DurableProcessWorkflowExecutor.ExecuteResumedAsync(f.Root, f.Id, default), "native duplicate claim");
        Check((await File.ReadAllLinesAsync(f.Effects, CancellationToken.None)).SequenceEqual(["a", "b"]),
            "Native resume repeated completed effects.");
        Check(!File.Exists(f.Report) && !File.Exists(Path.Combine(f.Output, "events.jsonl")),
            "Native process execution wrote legacy checkpoint or event authority.");

        OperationEventsPage suffix = await OperationEventReplay.ReadAsync(paths, new(f.Id, prior.NextSequence, 20), default);
        Check(suffix.Events.Count == 4 && suffix.Events[0].Type == "workflow_resumed" &&
            suffix.Events[^1].Type == "workflow_finished", "Native process event cursor failed after resume.");
        ProcessOperationReportReader.Verified report = await ProcessOperationReportReader.ReadVerifiedAsync(paths, f.Id, default);
        Check(report.Report.Status == "succeeded" && report.Report.ResumeCount == 1 && report.Projection is not null,
            "Native terminal report did not project successful recovery.");

        OperationArtifactList catalog = await ProcessArtifactReader.ListAsync(paths, f.Id, default);
        OperationArtifactInfo artifact = catalog.Artifacts.Single(item => item.ArtifactId == "report");
        OperationArtifactChunk chunk = await ProcessArtifactReader.ReadChunkAsync(paths,
            new(f.Id, artifact.ArtifactId, 0, 65536, artifact.Size, artifact.LastWriteUtcTicks), default);
        Check(chunk.Content.SequenceEqual(report.Projection!), "Native process artifact differs from its JSON report.");

        {
            OperationArtifactInfo nativeArtifact = catalog.Artifacts.Single(item => item.ArtifactId == "native-report");
            OperationArtifactChunk nativeChunk = await ProcessArtifactReader.ReadChunkAsync(paths,
                new(f.Id, nativeArtifact.ArtifactId, 0, 65536, nativeArtifact.Size, nativeArtifact.LastWriteUtcTicks), default);
            byte[] bytes = nativeChunk.Content;
            ProcessStartInfo info = new(capnp) { RedirectStandardInput = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "decode", schema, "ProcessCheckpoint" })
            {
                info.ArgumentList.Add(arg);
            }

            using Process decoder = Process.Start(info) ?? throw new UnreachableException("Could not run Cap'n Proto.");
            Task<string> output = decoder.StandardOutput.ReadToEndAsync();
            await decoder.StandardInput.BaseStream.WriteAsync(bytes);
            decoder.StandardInput.Close();
            await decoder.WaitForExitAsync();
            Check(decoder.ExitCode == 0 && (await output).Contains(f.Id, StringComparison.Ordinal),
                "Official Cap'n Proto could not decode retained process evidence.");
        }

        foreach (string point in new[] { "process-checkpoint-before-confirm", "process-checkpoint-confirmed",
                     "process-before-spawn", "process-after-spawn-before-observation",
                     "process-after-exit-before-observation", "process-child-completed" })
        {
            Fixture interrupted = Create(parent, native: true);
            await Crash(interrupted, false, point, point.StartsWith("process-checkpoint", StringComparison.Ordinal) ? null : "a");
            await VerifyInterruptedNativeProcessAsync(interrupted, point);
            using SqliteConnection db = new(new SqliteConnectionStringBuilder
            {
                DataSource = WorkflowLedgerPath.Resolve(interrupted.Root),
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            await db.OpenAsync();
            using SqliteCommand sql = db.CreateCommand();
            sql.CommandText = "SELECT count(*) FROM process_launch_intents";
            Check((long)(await sql.ExecuteScalarAsync())! == (point == "process-checkpoint-before-confirm" ? 0 : 1),
                "Native launch intent visibility disagrees with the write-ahead boundary.");
            sql.CommandText = "SELECT count(*) FROM process_lifecycle WHERE stage=3";
            Check((long)(await sql.ExecuteScalarAsync())! == (point == "process-child-completed" ? 1 : 0),
                "Native completion observation crossed the wrong process boundary.");
        }

        foreach (string boundary in new[] { "process-before-finalization", "process-after-finalization" })
        {
            Fixture terminal = Create(parent, native: true);
            await Crash(terminal, false, boundary);
            VerifyTerminalBoundary(terminal, boundary == "process-after-finalization");
            File.Delete(terminal.Manifest);
            Check(await DurableProcessWorkflowExecutor.ExecuteAcceptedAsync(terminal.Root, terminal.Id,
                terminal.Request, 1, default) == 0, "Exact retry could not recover native terminal evidence.");
            VerifyTerminalBoundary(terminal, committed: true);
            await Reject(() => DurableProcessWorkflowExecutor.ExecuteResumedAsync(terminal.Root, terminal.Id, default),
                boundary);
        }

        await VerifySurvivingChildAsync(parent);

        Fixture canceled = Create(parent, slow: true, native: true);
        using CancellationTokenSource stop = new();
        Task<int> running = DurableProcessWorkflowExecutor.ExecuteAcceptedAsync(canceled.Root, canceled.Id,
            canceled.Request, 1, stop.Token);
        using (CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20)))
        {
            while (!File.Exists(canceled.Effects) || !(await File.ReadAllLinesAsync(canceled.Effects, deadline.Token)).Contains("b"))
            {
                Check(!running.IsCompleted, "Native process exited before cancellation.");
                await Task.Delay(20, deadline.Token);
            }
        }

        await stop.CancelAsync();
        Check(await running == 130, "Native running process did not finalize cancellation.");
        using SqliteConnection cancellationLedger = new(new SqliteConnectionStringBuilder
        {
            DataSource = WorkflowLedgerPath.Resolve(canceled.Root),
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await cancellationLedger.OpenAsync();
        using SqliteCommand termination = cancellationLedger.CreateCommand();
        termination.CommandText = "SELECT count(*) FROM process_lifecycle WHERE node_id='b' AND stage IN (4,5);";
        Check((long)(await termination.ExecuteScalarAsync())! == 2, "Missing native termination request or confirmation evidence.");

        Console.WriteLine("Native process checkpoints, launch evidence, hard-kill recovery, events, and projections passed.");
    }

    internal static async Task RunAsync(string parent)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Process recovery SIGKILL suite currently requires Linux.");
        }

        foreach (string? point in new[] { "process-checkpoint-before-confirm", "process-checkpoint-confirmed" })
        {
            Fixture f = Create(parent);
            await Crash(f, false, point);
            using DurableAcceptanceStore store = f.Store();
            _ = store.Reconcile(f.Id, 0, "needs_attention", "Stopped before initial dispatch");
            await Reject(() => ProcessWorkflowRecovery.PrepareAsync(store, f.Id, new(f.Root), CancellationToken.None), point);
            Check(!File.Exists(f.Effects), "Unconfirmed initial dispatch produced an effect.");
        }

        // A failure before claim leaves the same explicit admission executable;
        // a committed claim, even before spawn, permanently forbids redispatch.
        foreach (string? point in new[] { "process-before-resume-claim", "process-after-resume-claim" })
        {
            Fixture f = await Partial(parent);
            await AdmitAsync(f);
            byte[] originalLog = await File.ReadAllBytesAsync(Path.Combine(f.Output, f.Read().Nodes[0].Stdout!), CancellationToken.None);
            await Crash(f, true, point);
            if (point == "process-before-resume-claim")
            {
                await File.WriteAllTextAsync(f.Manifest, "edited original; immutable snapshot must still execute", CancellationToken.None);
                Check(await DurableProcessWorkflowExecutor.ExecuteResumedAsync(f.Root, f.Id, default) == 0,
                    "Pre-claim crash consumed attempt.");
                Check((await File.ReadAllLinesAsync(f.Effects, CancellationToken.None)).SequenceEqual(["a", "b"]), "Resume duplicated a prerequisite.");
                Check((await File.ReadAllBytesAsync(Path.Combine(f.Output, f.Read().Nodes[0].Stdout!), CancellationToken.None)).SequenceEqual(originalLog),
                    "Completed output was overwritten.");
                using DurableAcceptanceStore store = f.Store();
                Check(store.GetFinalization(f.Id) is { State: "succeeded" } && f.Read().ResumeCount == 1,
                    "Resumed report did not finalize through SQLite.");
                long[] events = [.. (await File.ReadAllLinesAsync(Path.Combine(f.Output, "events.jsonl"), CancellationToken.None)).Select(line => JsonElement.Parse(line).GetProperty("sequence").GetInt64())];
                Check(events.Distinct().Count() == events.Length, "Resume reused journal sequence numbers.");
            }
            await Reject(() => DurableProcessWorkflowExecutor.ExecuteResumedAsync(f.Root, f.Id, default), point);
            Check((await File.ReadAllLinesAsync(f.Effects, CancellationToken.None)).Count(x => x == "a") == 1, "A repeated after duplicate resume.");
        }
        Console.WriteLine("Process recovery claim crash boundaries, completed prerequisites/logs and duplicate resume passed.");

        foreach (string? point in new[] { "process-checkpoint-before-confirm", "process-checkpoint-confirmed",
                     "process-child-completed", "child-started" })
        {
            Fixture f = await Partial(parent, point == "child-started");
            await AdmitAsync(f);
            await Crash(f, true, point, point == "process-child-completed" ? "b" : null,
                effect: point == "child-started");
            string[] before = await File.ReadAllLinesAsync(f.Effects, CancellationToken.None);
            await Reject(() => DurableProcessWorkflowExecutor.ExecuteResumedAsync(f.Root, f.Id, default), point);
            using DurableAcceptanceStore store = f.Store();
            await Reject(() => Task.FromResult(store.ClaimResumeAttempt(f.Id, f.Output)), point);
            Check((await File.ReadAllLinesAsync(f.Effects, CancellationToken.None)).SequenceEqual(before), "Uncertain node produced duplicate effects.");
            Check(store.GetFinalization(f.Id) is null, "Uncertain run was finalized.");
        }
        Console.WriteLine("Process persisted/unconfirmed dispatch, confirmed dispatch, child-start and missing completion checkpoint passed.");

        // No operator decision may turn an admitted side-effectful B into pending.
        Fixture unknown = Create(parent);
        await Crash(unknown, false, "process-child-completed", "a");
        using (DurableAcceptanceStore store = unknown.Store())
        {
            _ = store.Reconcile(unknown.Id, 0, "needs_attention", "Quiescent, but result remains unknown");
            await Reject(() => ProcessWorkflowRecovery.PrepareAsync(store, unknown.Id, new(unknown.Root), CancellationToken.None),
                "running node with operation-level reconciliation");
            ProcessOperationReport report = unknown.Read();
            report.Nodes[0].Status = "outcome_unknown";
            unknown.Write(report);
            await Reject(() => ProcessWorkflowRecovery.PrepareAsync(store, unknown.Id, new(unknown.Root), CancellationToken.None),
                "unresolved outcome_unknown");
            report.Nodes[0].Status = "pending";
            report.Nodes[0].StartedAt = null;
            report.Nodes[0].Stdout = report.Nodes[0].Stderr = null;
            unknown.Write(report);
            await Reject(() => ProcessWorkflowRecovery.PrepareAsync(store, unknown.Id, new(unknown.Root), CancellationToken.None),
                "formerly running node reset to pending");
        }

        // The child completed and its node checkpoint exists, but the terminal
        // workflow checkpoint was never written. Finalize without repeating it.
        Fixture completed = Create(parent);
        await Crash(completed, false, "process-before-terminal-checkpoint");
        await AdmitAsync(completed);
        Check(await DurableProcessWorkflowExecutor.ExecuteResumedAsync(completed.Root, completed.Id, default) == 0,
            "All-completed recovery failed.");
        Check((await File.ReadAllLinesAsync(completed.Effects, CancellationToken.None)).SequenceEqual(["a", "b"]), "All-completed recovery repeated effects.");
        await Tampering(parent);
        await CodecAndLeases(parent);
        Console.WriteLine("Process unresolved uncertainty, explicit reconciliation and tampering checks passed.");
    }

    private static async Task CodecAndLeases(string parent)
    {
        Fixture f = await Partial(parent);
        await AdmitAsync(f);
        byte[] original = await File.ReadAllBytesAsync(f.Report, CancellationToken.None);
        foreach (string? mode in new[] { "domain", "schemaVersion", "missing-domain", "duplicate-id", "status",
                     "unknown", "running", "metadata", "recoveryPhase", "resumeCount" })
        {
            JsonNode json = JsonNode.Parse(original)!;
            switch (mode)
            {
                case "domain": json["domain"] = "cmake"; break;
                case "schemaVersion": json["schemaVersion"] = 2; break;
                case "missing-domain": _ = json.AsObject().Remove("domain"); break;
                case "duplicate-id": json["nodes"]![1]!["id"] = "a"; break;
                case "status": json["nodes"]![1]!["status"] = "invented"; break;
                case "unknown": json["nodes"]![1]!["status"] = "outcome_unknown"; break;
                case "running": json["nodes"]![1]!["status"] = "running"; break;
                case "metadata": json["nodes"]![1]!["exitCode"] = 0; break;
                case "recoveryPhase": json["recoveryPhase"] = "resuming"; break;
                case "resumeCount": json["resumeCount"] = 1; break;
                default:
                    break;
            }
            await Reject(() => Task.FromResult(ProcessCheckpointEvidenceCodec.Instance.ForResume(
                System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()))), mode);
        }
        foreach (string? lockFile in new[] { Path.Combine(f.Root, ".cmakeworkflow", "execution-locks", f.Id + ".lock"),
                     Path.Combine(f.Output, ".workflow-run.lock") })
        {
            using (new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await Reject(() => DurableProcessWorkflowExecutor.ExecuteResumedAsync(f.Root, f.Id, default), "active lease");
            }

            Check((await File.ReadAllBytesAsync(f.Report, CancellationToken.None)).SequenceEqual(original), "Lease rejection mutated report.");
        }
        Check(await DurableProcessWorkflowExecutor.ExecuteResumedAsync(f.Root, f.Id, default) == 0,
            "Lease rejection consumed the resume attempt.");
        Check((await File.ReadAllLinesAsync(f.Effects, CancellationToken.None)).SequenceEqual(["a", "b"]), "Lease retries duplicated effects.");
    }

    private static async Task Tampering(string parent)
    {
        foreach (string? mode in new[] { "snapshot", "identity", "dependencies", "audit", "binding", "path", "legacy" })
        {
            Fixture f = await Partial(parent);
            await AdmitAsync(f);
            ProcessOperationReport report = f.Read();
            if (mode == "snapshot")
            {
                using SqliteConnection db = new("Data Source=" + Path.Combine(f.Root, ".cmakeworkflow", "operations.sqlite"));
                await db.OpenAsync();
                using SqliteCommand cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE accepted_workflows SET snapshot=X'7B7D';";
                _ = await cmd.ExecuteNonQueryAsync();
            }
            else if (mode == "identity")
            {
                await File.WriteAllTextAsync(f.Report, (await File.ReadAllTextAsync(f.Report, CancellationToken.None)).Replace(report.SnapshotSha256, new string('0', 64), StringComparison.Ordinal), CancellationToken.None);
            }
            else if (mode == "dependencies")
            {
                report.Nodes[1].Dependencies!.Clear();
                ProcessOperationReport prior = ProcessCheckpointEvidenceCodec.Parse(report.Recovery!.PreviousCheckpoint);
                prior.Nodes[1].Dependencies!.Clear();
                report.Recovery = report.Recovery with
                {
                    PreviousCheckpoint = JsonSerializer.SerializeToUtf8Bytes(prior,
                        ProcessWorkflowJson.Default.ProcessOperationReport)
                };
                f.Write(report);
            }
            else if (mode == "audit") { report.Recovery = null; f.Write(report); }
            else if (mode == "binding")
            {
                using SqliteConnection db = new("Data Source=" + Path.Combine(f.Root, ".cmakeworkflow", "operations.sqlite"));
                await db.OpenAsync();
                using SqliteCommand cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE dispatch_checkpoints SET checkpoint_sha256='changed';";
                _ = await cmd.ExecuteNonQueryAsync();
            }
            else if (mode == "legacy")
            {
                // Older v1 reports remain readable, but missing historical
                // dispatch bytes cannot prove that an eligible node never ran.
                using SqliteConnection db = new("Data Source=" + Path.Combine(f.Root, ".cmakeworkflow", "operations.sqlite"));
                await db.OpenAsync();
                using SqliteCommand cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE dispatch_checkpoints SET checkpoint_bytes=NULL;";
                _ = await cmd.ExecuteNonQueryAsync();
                // An already prepared audit still binds exact bytes, so test
                // missing evidence before preparation rather than after it.
                await File.WriteAllBytesAsync(f.Report, report.Recovery!.PreviousCheckpoint, CancellationToken.None);
                using DurableAcceptanceStore store = f.Store();
                await Reject(() => ProcessWorkflowRecovery.PrepareAsync(store, f.Id, new(f.Root), CancellationToken.None), mode);
                continue;
            }
            else
            {
                string outside = Path.Combine(parent, "outside-" + Guid.NewGuid().ToString("N"));
                File.Move(f.Report, outside);
                _ = File.CreateSymbolicLink(f.Report, outside);
            }
            await Reject(() => DurableProcessWorkflowExecutor.ExecuteResumedAsync(f.Root, f.Id, default), mode);
            Check((await File.ReadAllLinesAsync(f.Effects, CancellationToken.None)).SequenceEqual(["a"]), "Tampering caused a child dispatch.");
        }
    }

    internal static async Task WireAsync(string endpoint, string parent, CancellationToken ct)
    {
        Fixture first = await Partial(parent);
        // Independent operations in the SAME workspace/SQLite database, each
        // with its own immutable manifest, effects and reserved result tree.
        ProcessWorkflowManifest secondPlan = ProcessWorkflowDomain.ReadPlan(await File.ReadAllBytesAsync(first.Manifest, ct), new(first.Root));
        string effects = Path.Combine(first.Root, "second-effects.txt");
        foreach (ProcessWorkflowNode node in secondPlan.Nodes)
        {
            node.Arguments![2] = effects;
        }

        string manifest = Path.Combine(first.Root, "second-process.json");
        byte[] snapshot = JsonSerializer.SerializeToUtf8Bytes(secondPlan, ProcessWorkflowJson.Default.ProcessWorkflowManifest);
        await File.WriteAllBytesAsync(manifest, snapshot, CancellationToken.None);
        DurableImmutableRequest request = ProcessWorkflowDomain.Admission(new("process", "independent-process", manifest,
            ProcessWorkflowRecovery.Hash(snapshot), 1, Path.Combine(first.Root, "second-run")));
        Fixture second;
        using (DurableAcceptanceStore store = first.Store())
        {
            second = new(first.Root, manifest, request.ResultsDirectory, store.AcceptImmutable(request).OperationId, request, effects);
        }

        await Crash(second, false, "process-before-reserve", "b");
        async Task Submit(Fixture f)
        {
            using Stream wire = await LocalTransport.ConnectAsync(endpoint, ct);
            using LostResponseStream lossy = new(wire);
            using JsonRpc rpc = HostConnection.Create(lossy);
            IHostRpc client = rpc.Attach<IHostRpc>(); rpc.StartListening();
            _ = await client.InitializeAsync(new(1, f.Root), ct);
            await Reject(() => client.WorkflowResumeAsync(new(f.Id, 0, false), ct), "unconfirmed quiescence");
            _ = await client.RecoveryReconcileAsync(new(f.Id, 0, "needs_attention", "Checked external execution stopped", true), ct);
            await Reject(() => client.WorkflowResumeAsync(new(f.Id, 0, true), ct), "stale revision");
            _ = await client.WorkflowResumeAsync(new(f.Id, 1, true), ct);
            _ = await client.WorkflowResumeAsync(new(f.Id, 2, true), ct);
            // Suppress the actual transport response, then disconnect. The
            // submitting client never observes a successful resume receipt.
            lossy.DropResponse = true;
            Task<RecoveryInspectionView> ignoredReceipt = client.WorkflowResumeExecuteAsync(new(f.Id, 2, true), ct);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await lossy.Dropped.Task.WaitAsync(timeout.Token);
            _ = ignoredReceipt.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        await Task.WhenAll(Submit(first), Submit(second));
        foreach (Fixture? f in new[] { first, second })
        {
            using Stream wire = await LocalTransport.ConnectAsync(endpoint, ct);
            using JsonRpc rpc = HostConnection.Create(wire);
            IHostRpc client = rpc.Attach<IHostRpc>(); rpc.StartListening();
            _ = await client.InitializeAsync(new(1, f.Root), ct);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while ((await client.OperationGetAsync(new(f.Id), timeout.Token)).State != "succeeded")
            {
                await Task.Delay(20, timeout.Token);
            }

            Check((await client.ExecutionReportAsync(new(f.Id), ct)).GetProperty("resumeCount").GetInt32() == 1,
                "Wire process result lost recovery metadata.");
            await Reject(() => client.WorkflowResumeExecuteAsync(new(f.Id, 2, true), ct), "terminal duplicate");
            Check((await File.ReadAllLinesAsync(f.Effects, ct)).SequenceEqual(["a", "b"]), "Wire/reconnect duplicated effects.");
        }

        Fixture cancel = await Partial(parent, slow: true);
        using (Stream wire = await LocalTransport.ConnectAsync(endpoint, ct))
        using (JsonRpc rpc = HostConnection.Create(wire))
        {
            IHostRpc client = rpc.Attach<IHostRpc>(); rpc.StartListening();
            _ = await client.InitializeAsync(new(1, cancel.Root), ct);
            _ = await client.RecoveryReconcileAsync(new(cancel.Id, 0, "needs_attention", "Quiescent", true), ct);
            _ = await client.WorkflowResumeAsync(new(cancel.Id, 1, true), ct);
            _ = await client.WorkflowResumeExecuteAsync(new(cancel.Id, 2, true), ct);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (!(await File.ReadAllLinesAsync(cancel.Effects, ct)).Contains("b"))
            {
                await Task.Delay(20, timeout.Token);
            }
            // While active, a duplicate receipt must never create another runner.
            _ = await client.WorkflowResumeExecuteAsync(new(cancel.Id, 2, true), ct);
            _ = await client.OperationCancelAsync(new(cancel.Id, 0), ct);
            while ((await client.OperationGetAsync(new(cancel.Id), timeout.Token)).State != "canceled")
            {
                await Task.Delay(20, timeout.Token);
            }

            Check((await File.ReadAllLinesAsync(cancel.Effects, ct)).SequenceEqual(["a", "b"]), "Cancellation duplicated effects.");
        }
        Console.WriteLine("Process recovery wire: explicit admission, revision/quiescence checks, lost receipt/reconnect, concurrent identities, duplicate resume and cancellation passed.");
    }

    private sealed class LostResponseStream(Stream inner) : Stream
    {
        private readonly CancellationTokenSource stopped = new();
        private int disposed;
        internal bool DropResponse;
        internal TaskCompletionSource Dropped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopped.Token);
            int count = await inner.ReadAsync(buffer, linked.Token);
            if (DropResponse && count > 0)
            {
                _ = Dropped.TrySetResult();
                await Task.Delay(Timeout.Infinite, linked.Token);
            }
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                stopped.Cancel();
                stopped.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}