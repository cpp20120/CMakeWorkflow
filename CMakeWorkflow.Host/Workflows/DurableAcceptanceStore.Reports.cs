using System.Globalization;
using System.Text.Json;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Storage.Documents;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host.Workflows;

internal sealed record NativeReportReference(DocumentId Document, BlobId Blob, int SchemaVersion, long StoredLength);

public sealed partial class DurableAcceptanceStore
{
    private const string NativeWorkflowReportSchema = """

        CREATE TABLE workflow_terminal_reports (
            operation_id TEXT PRIMARY KEY REFERENCES operation_finalizations(operation_id),
            document_id TEXT NOT NULL CHECK(length(document_id)=64),
            blob_id TEXT NOT NULL CHECK(length(blob_id)=64),
            schema_version INTEGER NOT NULL CHECK(schema_version=1),
            stored_length INTEGER NOT NULL CHECK(stored_length BETWEEN 16 AND 16777224)
        ) STRICT, WITHOUT ROWID;
        """;

    internal NativeReportReference? ReadNativeReportReference(string operation, SqliteTransaction? tx = null)
    {
        if (!UsesNativeWorkflowReports)
        {
            return null;
        }

        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = "SELECT document_id,blob_id,schema_version,stored_length FROM workflow_terminal_reports WHERE operation_id=$operation;";
        _ = sql.Parameters.AddWithValue("$operation", operation);
        using SqliteDataReader row = sql.ExecuteReader();
        return row.Read() ? new(new(row.GetString(0)), new(row.GetString(1)), row.GetInt32(2), row.GetInt64(3)) : null;
    }

    private void InsertNativeReportReference(string operation, NativeReportReference reference, SqliteTransaction tx)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            INSERT INTO workflow_terminal_reports(operation_id,document_id,blob_id,schema_version,stored_length)
            VALUES($operation,$document,$blob,$version,$length);
            """;
        _ = sql.Parameters.AddWithValue("$operation", operation);
        _ = sql.Parameters.AddWithValue("$document", reference.Document.Value);
        _ = sql.Parameters.AddWithValue("$blob", reference.Blob.Value);
        _ = sql.Parameters.AddWithValue("$version", reference.SchemaVersion);
        _ = sql.Parameters.AddWithValue("$length", reference.StoredLength);
        _ = sql.ExecuteNonQuery();
    }

    internal async Task FinalizeNativeExecutionAsync(AcceptedWorkflow accepted, BinaryWorkflowInput binary,
        string directory, int exitCode, WorkflowReport report, CancellationToken ct)
    {
        StoredPlanAuthorization authorization = ReadPlanAuthorization(accepted.OperationId, null);
        NativeExecutionReport terminal = new(accepted.WorkspaceId, accepted.OperationId, accepted.RequestDigest,
            binary.Document.Id.Value, authorization.Document.Value, authorization.Identity, directory, exitCode, report,
            ReadCancellationTime(accepted.OperationId));
        VerifyNativeReportBinding(accepted, terminal);
        await OperationArtifactReader.VerifyTerminalArtifactsAsync(new WorkspaceReadPaths(workspace), terminal, ct);
        DocumentStore store = await OpenExecutionDocumentsAsync(ct);
        await RecoveryFaults.HitAsync("before-report-publication", null, ct);
        DocumentDescriptor descriptor = await store.PublishExecutionReportAsync(terminal, ReportRetention(accepted.OperationId), ct);
        await RecoveryFaults.HitAsync("before-terminal-commit", null, ct);
        CommitNativeExecution(terminal, descriptor);
    }

    // The caller holds the operation execution lease. Retrying the exact accepted request can
    // complete a published report after a crash without starting or resuming any external work.
    internal async Task<bool> CommitPublishedReportAsync(AcceptedWorkflow accepted, CancellationToken ct)
    {
        DocumentStore store = DocumentStore.OpenExistingReportStore(WorkflowLedgerPath.Documents(workspace));
        DocumentDescriptor? descriptor = await store.ResolveReferenceAsync(ReportRetention(accepted.OperationId), ct);
        if (descriptor is null)
        {
            return false;
        }

        using DocumentSession session = await store.OpenAsync(descriptor.Id, ct: ct);
        NativeExecutionReport report = session.MaterializeExecutionReport();
        VerifyNativeReportBinding(accepted, report);
        await VerifyStoredDispatchAsync(accepted.OperationId, ct);
        await OperationArtifactReader.VerifyTerminalArtifactsAsync(new WorkspaceReadPaths(workspace), report, ct);
        CommitNativeExecution(report, session.Descriptor);
        return true;
    }

    internal async Task<bool> HasPublishedNativeReportAsync(string operation, CancellationToken ct)
    {
        DocumentStore store = DocumentStore.OpenExistingReportStore(WorkflowLedgerPath.Documents(workspace));
        return await store.ResolveReferenceAsync(ReportRetention(operation), ct) is not null;
    }

    internal async Task<NativeExecutionReport> ReadNativeReportAsync(AcceptedWorkflow accepted, NativeReportReference reference,
        CancellationToken ct)
    {
        using DocumentSession session = await OpenNativeReportDocumentAsync(reference, ct);
        NativeExecutionReport report = session.MaterializeExecutionReport();
        VerifyNativeReportBinding(accepted, report);
        return report;
    }

    internal async Task<DocumentSession> OpenNativeReportDocumentAsync(NativeReportReference reference, CancellationToken ct)
    {
        DocumentStore store = DocumentStore.OpenExistingReportStore(WorkflowLedgerPath.Documents(workspace));
        DocumentSession session = await store.OpenAsync(reference.Document, ct: ct);
        if (session.Descriptor.Blob != reference.Blob || session.Descriptor.SchemaVersion != reference.SchemaVersion ||
            session.Descriptor.StoredLength != reference.StoredLength || session.Descriptor.Codec != DocumentCodec.ExecutionReportCapnp)
        {
            session.Dispose();
            throw new InvalidDataException("Terminal report differs from its committed reference.");
        }

        return session;
    }

    private void CommitNativeExecution(NativeExecutionReport terminal, DocumentDescriptor descriptor)
    {
        WorkflowReport report = terminal.Report;
        string projectionHash = Hex(ProjectWorkflowReport(report));
        _ = CommitFinalization(terminal.OperationId, terminal.RunDirectory, terminal.ExitCode,
            new(report.ManifestHash, report.Status, report.ResumeCount),
            projectionHash, new(descriptor.Id, descriptor.Blob, descriptor.SchemaVersion, descriptor.StoredLength));
    }

    internal static byte[] ProjectWorkflowReport(WorkflowReport report) =>
        JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);

    private void VerifyNativeReportBinding(AcceptedWorkflow accepted, NativeExecutionReport terminal)
    {
        StoredPlanAuthorization authorization = ReadPlanAuthorization(accepted.OperationId, null);
        BinaryWorkflowInput binary = ReadBinaryInput(accepted.OperationId)
            ?? throw new InvalidDataException("Terminal report has no accepted binary workflow.");
        if (terminal.WorkspaceId != accepted.WorkspaceId || terminal.OperationId != accepted.OperationId ||
            terminal.RequestDigest != accepted.RequestDigest || terminal.WorkflowDocumentId != binary.Document.Id.Value ||
            terminal.DispatchIdentity != authorization.Identity || terminal.PlanDocumentId != authorization.Document.Value ||
            !PathComparer.Equals(terminal.RunDirectory, binary.ResultsDirectory) ||
            !string.Equals(terminal.Report.ManifestHash, accepted.ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            terminal.Report.SourceSnapshotId != GetSourceSnapshotId(accepted.OperationId) ||
            (terminal.OperatorCancellationRequestedAt is { } canceled &&
             canceled != ReadCancellationTime(accepted.OperationId)))
        {
            throw new InvalidDataException("Terminal report evidence belongs to different execution.");
        }
    }

    private DateTimeOffset? ReadCancellationTime(string operation)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.CommandText = "SELECT requested_at FROM operation_cancellations WHERE operation_id=$operation;";
        _ = sql.Parameters.AddWithValue("$operation", operation);
        return sql.ExecuteScalar() is string timestamp
            ? DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture) : null;
    }

    private static string ReportRetention(string operation) => "execution-report/" + operation;
}
