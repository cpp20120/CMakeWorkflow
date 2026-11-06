using System.Globalization;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.History;
using CMakeWorkflow.History.Analysis;
using CMakeWorkflow.History.Models;
using CMakeWorkflow.History.Storage;
using CMakeWorkflow.Host.Execution;
using CMakeWorkflow.Host.History;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Workflows;
using Microsoft.Data.Sqlite;
using StreamJsonRpc;

namespace CMakeWorkflow.Host.Sessions;


#pragma warning disable CA1001 // The session gate uses only managed async waiters, never AvailableWaitHandle; DrainAsync preserves the existing session lifecycle.
public sealed partial class DoctorSession
{
    private static readonly string[] SqliteSidecarSuffixes =
    [
    "-wal",
    "-shm",
    "-journal"
];
    private static readonly string[] CompletedReportNames =
    [
    "run.json",
    "report.json",
    "campaign-completion.json"
];
    private static readonly string[] ReadCapabilities =
    [
        "context/inspect", "target/resolve", "workflow/plan", "workflow/preflight", "runs/get",
        "profile/prepare", "benchmark/prepare", "profileRecipe/prepare", "testMatrix/prepare", "buildMatrix/prepare",
        "benchmarkMatrix/prepare", "profile/analyze", "operations/find", "operations/get", "operations/list", "operations/cancel",
        "workflow/start", "execution/start", "execution/report", "workflow/recoveryInspect", "workflow/reconcile", "workflow/resume", "workflow/resumeExecute",
        "operations/events", "operations/overview", "operations/report", "operations/artifacts/list", "operations/artifacts/readChunk", "history/import", "history/runs", "history/show", "history/metrics", "history/query",
        "history/compare", "history/detect", "history/runsPage", "toolchains/discover", "toolchains/resolve",
        "toolchains/inspectEnvironment", "target/prepare", "profile/replay", "dagflow/mutation/run",
        "dagflow/weakMemory/run", "dagflow/fuzz/seed", "dagflow/fuzz/run", "dagflow/fuzz/triage", "dagflow/fuzz/merge"
    ];

    private const int MeasurementLimit = 50000;

    // Operation lifetime belongs to Host, not to the RPC request or connection.
    /// <summary>Authorizes workspace paths before committing durable workflow acceptance.</summary>
    /// <remarks>
    /// Checks initial RPC cancellation, then hands work to the Host coordinator without linking its token to this request.
    /// Cancellation/lost reply can race with acceptance and dispatch. The caller must find the saved request ID to recover the result.
    /// </remarks>
    public Task<OperationReference> WorkflowStartAsync(WorkflowStartRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null)
        {
            throw new LocalRpcException("Call initialize first.")
            { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        }

        if (durableExecutions is null)
        {
            throw new InvalidOperationException("durable_execution_unavailable");
        }

        WorkspaceReadPaths paths = new(workspace);
        string manifest = paths.Require(request.Manifest);
        string output = paths.Require(request.ResultsDirectory);
        // Accept commits the immutable manifest before any response or scheduling.
        // Request cancellation is intentionally not propagated after acceptance.
        return Task.FromResult(durableExecutions.Start(paths.WorkspaceDirectory,
            request with { Manifest = manifest, ResultsDirectory = output }));
    }

    /// <summary>Commits durable stop intent and signals Host-owned execution separately from this RPC.</summary>
    /// <remarks>
    /// Cancellation of this RPC cannot revoke a committed intent. Receipt Requested=true does not prove workers stopped.
    /// </remarks>
    public Task<OperationCancelResult> OperationCancelAsync(OperationCancelRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null)
        {
            throw new LocalRpcException("Call initialize first.")
            { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        }

        if (durableExecutions is null)
        {
            throw new InvalidOperationException("durable_execution_unavailable");
        }

        WorkspaceReadPaths paths = new(workspace);
        DurableAcceptanceStore.CancellationDecision result = durableExecutions.Cancel(paths.WorkspaceDirectory, request.OperationId, request.ExpectedRevision);
        return Task.FromResult(new OperationCancelResult(result.OperationId, result.State, result.Revision,
            result.Requested));
    }

    private static RecoveryInspectionView RecoveryView(DurableAcceptanceStore.RecoveryInspection i) =>
        new(i.OperationId, i.AcceptanceState, i.TerminalState, i.CancellationRequested,
            i.RunDirectory, i.CheckpointSha256, i.Decision, i.Revision, i.Note);

    public Task<RecoveryInspectionView> RecoveryInspectAsync(RecoveryInspectRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            string path = paths.Database(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite"));
            if (!File.Exists(path))
            {
                throw new KeyNotFoundException("Unknown operation.");
            }

            using DurableAcceptanceStore store = new(paths.WorkspaceDirectory, readOnly: true);
            return Task.FromResult(RecoveryView(store.InspectRecovery(request.OperationId)));
        }, cancellationToken);

    /// <summary>Records a recovery decision under the cross-process execution lease.</summary>
    /// <remarks>
    /// Requires explicit operator-confirmed quiescence and no active coordinator task. A lease cannot prove orphaned descendants stopped.
    /// Only commits the decision: does not dispatch, reset nodes or release results ownership.
    /// </remarks>
    public Task<RecoveryInspectionView> RecoveryReconcileAsync(RecoveryReconcileRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null)
        {
            throw new LocalRpcException("Call initialize first.")
            { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        }

        if (!request.ConfirmedQuiescent)
        {
            throw new ArgumentException("Operator must explicitly confirm external execution has stopped.");
        }

        if (durableExecutions is null)
        {
            throw new InvalidOperationException("durable_execution_unavailable");
        }

        WorkspaceReadPaths paths = new(workspace);
        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory);
        if (durableExecutions.IsActive(store.WorkspaceId, request.OperationId))
        {
            throw new InvalidOperationException("operation_execution_active");
        }
        // Cross-process lease is held by the executor for its entire lifetime.
        // A departed host cannot prove that orphaned grandchildren have exited:
        // ConfirmedQuiescent is an explicit operator assertion, not automatic detection.
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length != 32 ||
            !request.OperationId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid operation ID.");
        }

        string lockDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks"));
        _ = Directory.CreateDirectory(lockDirectory);
        string lockFile = paths.Require(Path.Combine(lockDirectory, request.OperationId + ".lock"));
        using FileStream lease = new(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return Task.FromResult(RecoveryView(store.Reconcile(request.OperationId,
            request.ExpectedRevision, request.Decision, request.Note)));
    }

    /// <summary>Admits resume under explicit quiescence confirmation without launching workers.</summary>
    /// <remarks>
    /// Requires an inactive operation and matching recovery revision. Process recovery preparation validates and durably
    /// annotates checkpoint evidence without resetting node outcomes. Cancellation/lost reply may follow committed admission.
    /// </remarks>
    public Task<RecoveryInspectionView> WorkflowResumeAsync(WorkflowResumeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null)
        {
            throw new LocalRpcException("Call initialize first.")
            { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        }

        if (!request.ConfirmedQuiescent)
        {
            throw new ArgumentException("Explicit quiescence confirmation is required.");
        }

        if (durableExecutions is null)
        {
            throw new InvalidOperationException("durable_execution_unavailable");
        }

        WorkspaceReadPaths paths = new(workspace);
        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory);
        if (store.GetDomainKind(request.OperationId) is not ("workflow" or "process"))
        {
            throw new InvalidOperationException("resume_unsupported_for_execution_domain");
        }

        if (durableExecutions.IsActive(store.WorkspaceId, request.OperationId))
        {
            throw new InvalidOperationException("operation_execution_active");
        }

        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length != 32 ||
            !request.OperationId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid operation ID.");
        }

        string lockDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks"));
        _ = Directory.CreateDirectory(lockDirectory);
        string lockFile = paths.Require(Path.Combine(lockDirectory, request.OperationId + ".lock"));
        using FileStream lease = new(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        DurableAcceptanceStore.RecoveryInspection current = store.InspectRecovery(request.OperationId);
        if (current.Revision != request.ExpectedRevision)
        {
            throw new InvalidOperationException("operation_revision_conflict");
        }

        if (store.GetDomainKind(request.OperationId) == "process")
        {
            ProcessWorkflowRecovery.Prepare(store, request.OperationId, paths);
        }

        return Task.FromResult(RecoveryView(store.AdmitResume(request.OperationId, request.ExpectedRevision)));
    }

    // Execute an already admitted resume. Unlike workflow/resume, this is a mutation
    // that can launch external processes. The one-shot SQLite attempt claim is the
    // crash boundary; RPC cancellation must never cancel a committed dispatch.
    /// <summary>Schedules an admitted recovery attempt under the Host coordinator lifetime.</summary>
    /// <remarks>
    /// Requires revision 2 and prior quiescence confirmation. The request token only guards admission, not committed dispatch.
    /// The executor permanently claims a single attempt before external launch; lost replies never authorize blind redispatch.
    /// </remarks>
    public async Task<RecoveryInspectionView> WorkflowResumeExecuteAsync(WorkflowResumeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null)
        {
            throw new LocalRpcException("Call initialize first.")
            { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        }

        if (!request.ConfirmedQuiescent)
        {
            throw new ArgumentException("Explicit quiescence confirmation is required.");
        }

        if (request.ExpectedRevision != 2)
        {
            throw new InvalidOperationException("resume_execution_requires_admitted_revision");
        }

        if (durableExecutions is null)
        {
            throw new InvalidOperationException("durable_execution_unavailable");
        }

        WorkspaceReadPaths paths = new(workspace);
        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory);
        if (store.GetDomainKind(request.OperationId) is not ("workflow" or "process"))
        {
            throw new InvalidOperationException("resume_unsupported_for_execution_domain");
        }

        DurableAcceptanceStore.RecoveryInspection inspected = store.InspectRecovery(request.OperationId);
        if (inspected.Revision != 2 || inspected.Decision != "needs_attention" ||
            inspected.TerminalState is not null || inspected.CancellationRequested)
        {
            throw new InvalidOperationException("resume_not_admitted_or_bound");
        }

        (string? RunDirectory, string? CheckpointSha256) = store.GetDispatchCheckpoint(request.OperationId)
                      ?? throw new InvalidOperationException("resume_checkpoint_missing");
        _ = paths.Require(Path.Combine(RunDirectory, "run.json"));
        if (!durableExecutions.IsActive(store.WorkspaceId, request.OperationId))
        {
            if (request.OperationId.Length != 32 || !request.OperationId.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("Invalid operation ID.");
            }

            string lockDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks"));
            _ = Directory.CreateDirectory(lockDirectory);
            using FileStream lease = new(paths.Require(Path.Combine(lockDirectory, request.OperationId + ".lock")),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (store.GetDomainKind(request.OperationId) == "process")
            {
                // Read-only verification here: admission prepared the checkpoint.
                // Claim and all mutation stay inside the executor's leases.
                string checkpoint = paths.Require(Path.Combine(RunDirectory, "run.json"));
                paths.Read(checkpoint);
                byte[] bytes = await File.ReadAllBytesAsync(checkpoint, cancellationToken);
                OperationCheckpointEvidence evidence = ProcessCheckpointEvidenceCodec.Instance.ForResume(bytes);
                if (!string.Equals(evidence.ResumeSourceCheckpointSha256, CheckpointSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("process_recovery_binding_changed");
                }
            }
        }
        // The coordinator owns execution beyond this RPC connection. An already
        // active attempt is an idempotent retry, not a second dispatch.
        durableExecutions.Resume(paths.WorkspaceDirectory, request.OperationId);
        return RecoveryView(inspected);
    }

    public Task<OperationEventsPage> OperationEventsAsync(OperationEventsRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => OperationEventReplay.ReadAsync(paths, request, ct), cancellationToken);

    public Task<OperationListPage> OperationListAsync(OperationListRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            DurableAcceptanceStore.ValidateListQuery(request);
            // Listing is read-only. A fresh workspace must not get an SQLite store as a side effect.
            string storePath = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite");
            if (!File.Exists(storePath))
            {
                return request.Cursor is not null
                    ? throw new ArgumentException("Cursor does not belong to this workspace.")
                    : Task.FromResult(new OperationListPage(1, [], null));
            }
            _ = paths.Database(storePath);
            using DurableAcceptanceStore store = new(paths.WorkspaceDirectory, readOnly: true);
            return Task.FromResult(store.ListOperations(request, ct));
        }, cancellationToken);

    public Task<OperationReference> OperationFindAsync(OperationFindRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => LookupOperation(paths, request.ClientRequestId, byRequestId: true, ct),
            cancellationToken);

    public Task<OperationReference>
        OperationGetAsync(OperationGetRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => LookupOperation(paths, request.OperationId, byRequestId: false, ct),
            cancellationToken);

    private static Task<OperationReference> LookupOperation(WorkspaceReadPaths paths, string id, bool byRequestId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
        {
            throw new ArgumentException("Operation identifier must contain 1..128 non-whitespace characters.");
        }
        // A query must never initialize an operations store or mutate workspace state.
        string storePath = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite");
        if (!File.Exists(storePath))
        {
            throw new KeyNotFoundException("Unknown operation.");
        }

        _ = paths.Database(storePath);
        using DurableAcceptanceStore store = new(paths.WorkspaceDirectory, readOnly: true);
        AcceptedWorkflow? accepted = byRequestId ? store.Find(id) : store.Get(id);
        return accepted is null
            ? throw new KeyNotFoundException("Unknown operation.")
            : Task.FromResult(new OperationReference(accepted.WorkspaceId, accepted.OperationId,
            accepted.ClientRequestId, store.GetDomainKind(accepted.OperationId), accepted.State,
            accepted.ManifestSha256,
            accepted.ManifestBaseDirectory));
    }


    public Task<OperationOverview> OperationOverviewAsync(OperationGetRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => OperationOverviewReader.ReadAsync(paths, request.OperationId, ct),
            cancellationToken);

    public Task<WorkflowReportSnapshot> OperationReportAsync(OperationGetRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => OperationArtifactReader.ReportAsync(paths, request.OperationId, ct),
            cancellationToken);

    public Task<OperationArtifactList> OperationArtifactListAsync(OperationArtifactListRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => OperationArtifactReader.ListAsync(paths, request.OperationId, ct),
            cancellationToken);

    public Task<OperationArtifactChunk> OperationArtifactReadAsync(OperationArtifactChunkRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => OperationArtifactReader.ReadChunkAsync(paths, request, ct),
            cancellationToken);

    public Task<ObservedBuildContext> ContextInspectAsync(ContextInspectRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request,
            (paths, ct) => CMakeInspection.InspectAsync(paths.Require(request.BuildDirectory), ct, paths.Read),
            cancellationToken);

    public Task<TargetResolutionView> TargetResolveAsync(TargetResolveRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => WorkflowQueries.ResolveAsync(paths.Require(request.BuildDirectory),
            request.Target,
            request.Configuration, ct, paths.Read), cancellationToken);

    public Task<WorkflowPlanView> WorkflowPlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, async (paths, ct) =>
        {
            if (request.Only is { Length: > 1000 } || request.Only?.Any(string.IsNullOrWhiteSpace) == true)
            {
                throw new ArgumentException("only must contain at most 1000 nonempty node IDs.");
            }
#if RPC_CANCELLATION_TESTING
            await RpcCancellationTestGate.WaitAsync(paths, request.Manifest, cancellationToken, disconnected, ct);
#endif
            string? controller = request.ControllerBuildDirectory is { } value ? paths.Require(value) : null;
            return WorkflowQueries.PlanView(await WorkflowPlanner.LoadAsync(paths.Require(request.Manifest),
                request.Only ?? [], controller, ct, paths.Read));
        }, cancellationToken);

    public Task<WorkflowReportSnapshot> RunGetAsync(RunGetRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request,
            (paths, ct) => WorkflowQueries.ReportAsync(paths.Require(request.RunDirectory), ct, paths.Read),
            cancellationToken);

    public Task<HistoryImportResult> HistoryImportAsync(HistoryImportRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, (paths, ct) =>
        {
            string database = paths.Require(request.DatabasePath);
            foreach (string? suffix in SqliteSidecarSuffixes)
            {
                _ = paths.Require(database + suffix);
            }

            string directory = paths.Require(request.Directory);
            ct.ThrowIfCancellationRequested();
            using HistoryStore store = new(database);
            HistoryImportResult? result = null;
            _ = store.Import(directory, request.Recursive, value => result = value,
                candidate =>
                {
                    ct.ThrowIfCancellationRequested();
                    _ = paths.Require(candidate);
                    foreach (string? name in CompletedReportNames)
                    {
                        _ = paths.Require(Path.Combine(candidate, name));
                    }

                    foreach (string file in Directory.EnumerateFiles(candidate, "*.results.json"))
                    {
                        _ = paths.Require(file);
                    }
                });
            return Task.FromResult(result ?? throw new InvalidDataException("Import produced no summary."));
        }, cancellationToken);

    public Task<HistoryRunPage> HistoryRunsPageAsync(
        HistoryRunsPageRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            if (request.Limit is < 1 or > 200)
            {
                throw new ArgumentException("limit must be between 1 and 200.");
            }

            string database = paths.Database(request.DatabasePath);
            using HistoryStore db = new(database, readOnly: true);
            return Task.FromResult(db.RunsPage(database, request.Limit, request.Status,
                Date(request.Since), Date(request.Until), request.Workflow, request.Variant,
                request.Cursor, paths.Contains, ct));
        }, cancellationToken);

    public Task<List<HistoryRun>> HistoryRunsAsync(HistoryRunsRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            if (request.Limit is < 1 or > 1000)
            {
                throw new ArgumentException("limit must be between 1 and 1000.");
            }

            using HistoryStore db = new(paths.Database(request.DatabasePath), readOnly: true);
            return Task.FromResult(db.Runs(request.Limit, request.Status, Date(request.Since), Date(request.Until),
                request.Workflow, request.Variant, paths.Contains, ct));
        }, cancellationToken);

    public Task<HistoryDetail> HistoryShowAsync(HistoryShowRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            using HistoryStore db = new(paths.Database(request.DatabasePath), readOnly: true);
            return Task.FromResult(Detail(db, request.RunId, paths, ct));
        }, cancellationToken);

    public Task<List<HistoryMeasurement>> HistoryMetricsAsync(HistoryMetricsRequest request,
        CancellationToken cancellationToken) =>
        MetricsAsync(request, false, cancellationToken);

    public Task<List<HistoryMeasurement>> HistoryQueryAsync(HistoryMetricsRequest request,
        CancellationToken cancellationToken) =>
        MetricsAsync(request, true, cancellationToken);

    private Task<List<HistoryMeasurement>> MetricsAsync(HistoryMetricsRequest request, bool query,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            if (query ? string.IsNullOrWhiteSpace(request.Metric) : string.IsNullOrWhiteSpace(request.RunId))
            {
                throw new ArgumentException(query ? "metric is required." : "runId is required.");
            }

            using HistoryStore db = new(paths.Database(request.DatabasePath), readOnly: true);
            if (request.RunId is not null)
            {
                _ = Detail(db, request.RunId, paths, ct);
            }

            return Task.FromResult(db.Metrics(request.RunId, request.Metric, request.Variant, request.Scenario,
                paths.Contains, MeasurementLimit, ct));
        }, cancellationToken);

    public Task<List<HistoryDifference>> HistoryCompareAsync(HistoryCompareRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            using HistoryStore db = new(paths.Database(request.DatabasePath), readOnly: true);
            return Task.FromResult(HistoryComparison.Compare(Detail(db, request.BaselineRunId, paths, ct),
                Detail(db, request.CandidateRunId, paths, ct)));
        }, cancellationToken);

    public Task<HistoryDetectionReport> HistoryDetectAsync(HistoryDetectRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            using HistoryStore db = new(paths.Database(request.DatabasePath), readOnly: true);
            return Task.FromResult(HistoryDetector.Detect(Detail(db, request.BaselineRunId, paths, ct),
                Detail(db, request.CandidateRunId, paths, ct), request.Metric, request.Variant, request.Scenario,
                request.Direction, request.MinimumEffectPercent, request.Alpha, ct));
        }, cancellationToken);

    private static HistoryDetail Detail(HistoryStore db, string id, WorkspaceReadPaths paths, CancellationToken ct)
    {
        return string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("runId is required.")
            : db.Show(id, path => paths.Require(path), MeasurementLimit, ct, paths.Contains);
    }

    private static string? Date(string? value)
    {
        return value is null
            ? null
            : !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                out DateTimeOffset parsed)
            ? throw new ArgumentException("History time filters require ISO timestamps.")
            : parsed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    private Task<T> ReadAsync<T>(object request, Func<WorkspaceReadPaths, CancellationToken, Task<T>> query,
        CancellationToken ct) =>
        ApplicationCallAsync(request, query, ct);

    /// <summary>Runs a gated application request with linked request/connection cancellation and bounded error translation.</summary>
    /// <remarks>
    /// Serializes calls using this helper through the per-session gate; it is not a Host-wide or cross-process lock,
    /// and durable admission endpoints have their own paths. Synchronous filesystem/SQLite work is moved off the RPC
    /// dispatch loop with Task.Run. Cancellation is cooperative: synchronous work can commit before noticing it.
    /// Known domain/I/O/storage errors become code-1001 ApplicationError refusals; cancellation and unexpected errors propagate.
    /// </remarks>
    /// <param name="request">Non-null request; initialization is required before the callback can run.</param>
    /// <param name="query">Callback receiving workspace policy and the linked lifetime; may read or mutate according to its endpoint.</param>
    /// <param name="ct">RPC request token linked with disconnect only for this application call.</param>
    private async Task<T> ApplicationCallAsync<T>(object request,
        Func<WorkspaceReadPaths, CancellationToken, Task<T>> query, CancellationToken ct)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, disconnected);
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (workspace is null)
            {
                throw new ApplicationRefusal("not_initialized", "Call initialize first.");
            }

            await gate.WaitAsync(lifetime.Token);
            try
            {
                // SQLite reads and filesystem metadata can be synchronous. Never
                // run them on the RPC dispatch loop or change global cwd/env.
                return await Task.Run(() => query(new(workspace), lifetime.Token), lifetime.Token);
            }
            finally
            {
                _ = gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is ApplicationRefusal or ArgumentException or IOException
                                          or JsonException or InvalidDataException or InvalidOperationException
                                          or UnauthorizedAccessException or KeyNotFoundException or SqliteException)
        {
            string code = error switch
            {
                ApplicationRefusal refusal => refusal.Code,
                FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => "not_found",
                UnauthorizedAccessException => "access_denied",
                ArgumentException => "invalid_argument",
                InvalidDataException or JsonException => "invalid_data",
                InvalidOperationException => "invalid_context",
                _ => "read_failed"
            };
            throw new LocalRpcException(error.Message)
            { ErrorCode = 1001, ErrorData = new ApplicationError(code, error.Message) };
        }
    }
}
#pragma warning restore CA1001
