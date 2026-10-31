using System.Globalization;
using System.Text.Json;
using CMakeWorkflow.History;
using Microsoft.Data.Sqlite;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    private static readonly string[] ReadCapabilities = ["context/inspect", "target/resolve", "workflow/plan", "runs/get",
        "operations/find", "operations/get", "operations/cancel", "workflow/start", "workflow/recoveryInspect", "workflow/reconcile", "workflow/resume", "workflow/resumeExecute", "operations/events", "history/runs", "history/show", "history/metrics", "history/query", "history/compare", "history/detect"];
    private const int MeasurementLimit = 50000;

    // Operation lifetime belongs to Host, not to the RPC request or connection.
    public Task<OperationReference> WorkflowStartAsync(WorkflowStartRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null) throw new LocalRpcException("Call initialize first.")
        { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        if (durableExecutions is null) throw new InvalidOperationException("durable_execution_unavailable");
        var paths = new WorkspaceReadPaths(workspace);
        var manifest = paths.Require(request.Manifest);
        var output = paths.Require(request.ResultsDirectory);
        // Accept commits the immutable manifest before any response or scheduling.
        // Request cancellation is intentionally not propagated after acceptance.
        return Task.FromResult(durableExecutions.Start(paths.WorkspaceDirectory,
            request with { Manifest = manifest, ResultsDirectory = output }));
    }

    public Task<OperationCancelResult> OperationCancelAsync(OperationCancelRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null) throw new LocalRpcException("Call initialize first.")
        { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        if (durableExecutions is null) throw new InvalidOperationException("durable_execution_unavailable");
        var paths = new WorkspaceReadPaths(workspace);
        var result = durableExecutions.Cancel(paths.WorkspaceDirectory, request.OperationId, request.ExpectedRevision);
        return Task.FromResult(new OperationCancelResult(result.OperationId, result.State, result.Revision, result.Requested));
    }

    private static RecoveryInspectionView RecoveryView(DurableAcceptanceStore.RecoveryInspection i) =>
        new(i.OperationId, i.AcceptanceState, i.TerminalState, i.CancellationRequested,
            i.RunDirectory, i.CheckpointSha256, i.Decision, i.Revision, i.Note);

    public Task<RecoveryInspectionView> RecoveryInspectAsync(RecoveryInspectRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var path = paths.Database(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite"));
            if (!File.Exists(path)) throw new KeyNotFoundException("Unknown operation.");
            using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory, readOnly: true);
            return Task.FromResult(RecoveryView(store.InspectRecovery(request.OperationId)));
        }, cancellationToken);

    public Task<RecoveryInspectionView> RecoveryReconcileAsync(RecoveryReconcileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null) throw new LocalRpcException("Call initialize first.")
        { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        if (!request.ConfirmedQuiescent)
            throw new ArgumentException("Operator must explicitly confirm external execution has stopped.");
        if (durableExecutions is null) throw new InvalidOperationException("durable_execution_unavailable");
        var paths = new WorkspaceReadPaths(workspace);
        using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory);
        if (durableExecutions.IsActive(store.WorkspaceId, request.OperationId))
            throw new InvalidOperationException("operation_execution_active");
        // Cross-process lease is held by the executor for its entire lifetime.
        // A departed host cannot prove that orphaned grandchildren have exited:
        // ConfirmedQuiescent is an explicit operator assertion, not automatic detection.
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length != 32 ||
            !request.OperationId.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid operation ID.");
        var lockFile = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks",
            request.OperationId + ".lock");
        using var lease = File.Exists(lockFile)
            ? new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            : null;
        return Task.FromResult(RecoveryView(store.Reconcile(request.OperationId,
            request.ExpectedRevision, request.Decision, request.Note)));
    }

    public Task<RecoveryInspectionView> WorkflowResumeAsync(WorkflowResumeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null) throw new LocalRpcException("Call initialize first.")
        { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        if (!request.ConfirmedQuiescent)
            throw new ArgumentException("Explicit quiescence confirmation is required.");
        if (durableExecutions is null) throw new InvalidOperationException("durable_execution_unavailable");
        var paths = new WorkspaceReadPaths(workspace);
        using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory);
        if (durableExecutions.IsActive(store.WorkspaceId, request.OperationId))
            throw new InvalidOperationException("operation_execution_active");
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length != 32 ||
            !request.OperationId.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid operation ID.");
        var lockFile = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "execution-locks",
            request.OperationId + ".lock");
        using var lease = File.Exists(lockFile)
            ? new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            : null;
        return Task.FromResult(RecoveryView(store.AdmitResume(request.OperationId, request.ExpectedRevision)));
    }

    // Execute an already admitted resume. Unlike workflow/resume, this is a mutation
    // that can launch external processes. The one-shot SQLite attempt claim is the
    // crash boundary; RPC cancellation must never cancel a committed dispatch.
    public Task<RecoveryInspectionView> WorkflowResumeExecuteAsync(WorkflowResumeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null) throw new LocalRpcException("Call initialize first.")
        { ErrorCode = 1001, ErrorData = new ApplicationError("not_initialized", "Call initialize first.") };
        if (!request.ConfirmedQuiescent)
            throw new ArgumentException("Explicit quiescence confirmation is required.");
        if (request.ExpectedRevision != 2)
            throw new InvalidOperationException("resume_execution_requires_admitted_revision");
        if (durableExecutions is null) throw new InvalidOperationException("durable_execution_unavailable");
        var paths = new WorkspaceReadPaths(workspace);
        using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory);
        var inspected = store.InspectRecovery(request.OperationId);
        if (inspected.Revision != 2 || inspected.Decision != "needs_attention" ||
            inspected.TerminalState is not null || inspected.CancellationRequested)
            throw new InvalidOperationException("resume_not_admitted_or_bound");
        var binding = store.GetDispatchCheckpoint(request.OperationId)
            ?? throw new InvalidOperationException("resume_checkpoint_missing");
        paths.Require(Path.Combine(binding.RunDirectory, "run.json"));
        // The coordinator owns execution beyond this RPC connection. An already
        // active attempt is an idempotent retry, not a second dispatch.
        durableExecutions.Resume(paths.WorkspaceDirectory, request.OperationId);
        return Task.FromResult(RecoveryView(inspected));
    }

    public Task<OperationEventsPage> OperationEventsAsync(OperationEventsRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => OperationEventReplay.ReadAsync(paths, request, ct), cancellationToken);

    public Task<OperationReference> OperationFindAsync(OperationFindRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => LookupOperation(paths, request.ClientRequestId, byRequestId: true, ct), cancellationToken);

    public Task<OperationReference> OperationGetAsync(OperationGetRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => LookupOperation(paths, request.OperationId, byRequestId: false, ct), cancellationToken);

    private static Task<OperationReference> LookupOperation(WorkspaceReadPaths paths, string id, bool byRequestId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            throw new ArgumentException("Operation identifier must contain 1..128 non-whitespace characters.");
        // A query must never initialize an operations store or mutate workspace state.
        var storePath = Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "operations.sqlite");
        if (!File.Exists(storePath)) throw new KeyNotFoundException("Unknown operation.");
        paths.Database(storePath);
        using var store = new DurableAcceptanceStore(paths.WorkspaceDirectory, readOnly: true);
        var accepted = byRequestId ? store.Find(id) : store.Get(id);
        if (accepted is null) throw new KeyNotFoundException("Unknown operation.");
        return Task.FromResult(new OperationReference(accepted.WorkspaceId, accepted.OperationId,
            accepted.ClientRequestId, "workflow", accepted.State, accepted.ManifestSha256, accepted.ManifestBaseDirectory));
    }


    public Task<ObservedBuildContext> ContextInspectAsync(ContextInspectRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => CMakeInspection.InspectAsync(paths.Require(request.BuildDirectory), ct, paths.Read), cancellationToken);

    public Task<TargetResolutionView> TargetResolveAsync(TargetResolveRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => WorkflowQueries.ResolveAsync(paths.Require(request.BuildDirectory), request.Target,
            request.Configuration, ct, paths.Read), cancellationToken);

    public Task<WorkflowPlanView> WorkflowPlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, async (paths, ct) =>
        {
            if (request.Only is { Length: > 1000 } || request.Only?.Any(string.IsNullOrWhiteSpace) == true)
                throw new ArgumentException("only must contain at most 1000 nonempty node IDs.");
#if RPC_CANCELLATION_TESTING
            await RpcCancellationTestGate.WaitAsync(paths, request.Manifest, cancellationToken, disconnected, ct);
#endif
            var controller = request.ControllerBuildDirectory is { } value ? paths.Require(value) : null;
            return WorkflowQueries.PlanView(await WorkflowPlanner.LoadAsync(paths.Require(request.Manifest),
                request.Only ?? [], controller, ct, paths.Read));
        }, cancellationToken);

    public Task<WorkflowReportSnapshot> RunGetAsync(RunGetRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) => WorkflowQueries.ReportAsync(paths.Require(request.RunDirectory), ct, paths.Read), cancellationToken);

    public Task<List<HistoryRun>> HistoryRunsAsync(HistoryRunsRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            if (request.Limit is < 1 or > 1000) throw new ArgumentException("limit must be between 1 and 1000.");
            using var db = new HistoryStore(paths.Database(request.DatabasePath), readOnly: true);
            return Task.FromResult(db.Runs(request.Limit, request.Status, Date(request.Since), Date(request.Until),
                request.Workflow, request.Variant, paths.Contains, ct));
        }, cancellationToken);

    public Task<HistoryDetail> HistoryShowAsync(HistoryShowRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            using var db = new HistoryStore(paths.Database(request.DatabasePath), readOnly: true);
            return Task.FromResult(Detail(db, request.RunId, paths, ct));
        }, cancellationToken);

    public Task<List<HistoryMeasurement>> HistoryMetricsAsync(HistoryMetricsRequest request, CancellationToken cancellationToken) =>
        MetricsAsync(request, false, cancellationToken);
    public Task<List<HistoryMeasurement>> HistoryQueryAsync(HistoryMetricsRequest request, CancellationToken cancellationToken) =>
        MetricsAsync(request, true, cancellationToken);

    private Task<List<HistoryMeasurement>> MetricsAsync(HistoryMetricsRequest request, bool query, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            if (query ? string.IsNullOrWhiteSpace(request.Metric) : string.IsNullOrWhiteSpace(request.RunId))
                throw new ArgumentException(query ? "metric is required." : "runId is required.");
            using var db = new HistoryStore(paths.Database(request.DatabasePath), readOnly: true);
            if (request.RunId is not null) Detail(db, request.RunId, paths, ct);
            return Task.FromResult(db.Metrics(request.RunId, request.Metric, request.Variant, request.Scenario,
                paths.Contains, MeasurementLimit, ct));
        }, cancellationToken);

    public Task<List<HistoryDifference>> HistoryCompareAsync(HistoryCompareRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            using var db = new HistoryStore(paths.Database(request.DatabasePath), readOnly: true);
            return Task.FromResult(HistoryComparison.Compare(Detail(db, request.BaselineRunId, paths, ct),
                Detail(db, request.CandidateRunId, paths, ct)));
        }, cancellationToken);

    public Task<HistoryDetectionReport> HistoryDetectAsync(HistoryDetectRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, (paths, ct) =>
        {
            using var db = new HistoryStore(paths.Database(request.DatabasePath), readOnly: true);
            return Task.FromResult(HistoryDetector.Detect(Detail(db, request.BaselineRunId, paths, ct),
                Detail(db, request.CandidateRunId, paths, ct), request.Metric, request.Variant, request.Scenario,
                request.Direction, request.MinimumEffectPercent, request.Alpha, ct));
        }, cancellationToken);

    private static HistoryDetail Detail(HistoryStore db, string id, WorkspaceReadPaths paths, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("runId is required.");
        return db.Show(id, path => paths.Require(path), MeasurementLimit, ct, paths.Contains);
    }
    private static string? Date(string? value)
    {
        if (value is null) return null;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            throw new ArgumentException("History time filters require ISO timestamps.");
        return parsed.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    private async Task<T> ReadAsync<T>(object request, Func<WorkspaceReadPaths, CancellationToken, Task<T>> query, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, disconnected);
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (workspace is null) throw new ApplicationRefusal("not_initialized", "Call initialize first.");
            await gate.WaitAsync(lifetime.Token);
            try
            {
                // SQLite reads and filesystem metadata can be synchronous. Never
                // run them on the RPC dispatch loop or change global cwd/env.
                return await Task.Run(() => query(new(workspace), lifetime.Token), lifetime.Token);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is ApplicationRefusal or ArgumentException or IOException
            or JsonException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or KeyNotFoundException or SqliteException)
        {
            var code = error switch
            {
                ApplicationRefusal refusal => refusal.Code,
                FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => "not_found",
                UnauthorizedAccessException => "access_denied",
                ArgumentException => "invalid_argument",
                InvalidDataException or JsonException => "invalid_data",
                InvalidOperationException => "invalid_context",
                _ => "read_failed"
            };
            throw new LocalRpcException(error.Message) { ErrorCode = 1001, ErrorData = new ApplicationError(code, error.Message) };
        }
    }
}
