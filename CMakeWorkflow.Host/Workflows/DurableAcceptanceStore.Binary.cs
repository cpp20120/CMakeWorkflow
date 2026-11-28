using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Host.History;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Storage.Documents;
using CMakeWorkflow.Sources;
using CMakeWorkflow.Tooling.Tools;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host.Workflows;

internal sealed record BinaryWorkflowInput(DocumentDescriptor Document, string ManifestPath, string ResultsDirectory, int Jobs);
internal sealed record StoredPlanAuthorization(DocumentId Document, string Identity);

public sealed partial class DurableAcceptanceStore
{
    private const int BinaryApplicationId = 0x434D5742;
    private const int InlineBindingLedgerVersion = 2;
    private const int DocumentPlanLedgerVersion = 3;
    private const int SourceManifestArtifactLedgerVersion = 4;
    private const int NativeEventLedgerVersion = 5;
    private const int NativeReportLedgerVersion = 6;
    private const int NativeCheckpointLedgerVersion = 7;
    private const int NativeProcessLedgerVersion = 8;
    private const int NativeProfileLedgerVersion = 9;

    internal bool BinaryLedger { get; }
    private int binaryLedgerVersion;
    internal bool UsesDocumentPlans => binaryLedgerVersion >= DocumentPlanLedgerVersion;
    internal bool AllowsSourceManifestArtifacts => binaryLedgerVersion >= SourceManifestArtifactLedgerVersion;
    internal bool UsesNativeWorkflowEvents => binaryLedgerVersion >= NativeEventLedgerVersion;
    internal bool UsesNativeWorkflowReports => binaryLedgerVersion >= NativeReportLedgerVersion;
    internal bool UsesNativeWorkflowCheckpoints => binaryLedgerVersion >= NativeCheckpointLedgerVersion;
    internal bool UsesNativeProcessCheckpoints => binaryLedgerVersion >= NativeProcessLedgerVersion;
    internal bool UsesNativeProfileAnalysis => binaryLedgerVersion >= NativeProfileLedgerVersion;
    private bool UsesStableProcessIdentity => binaryLedgerVersion >= NativeProfileLedgerVersion;
    internal int? NativeFormatEpoch => BinaryLedger ? binaryLedgerVersion : null;

    private void OpenBinaryLedgerEpoch(bool readOnly)
    {
        using SqliteTransaction? tx = readOnly ? null : connection.BeginTransaction();
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = "PRAGMA application_id;";
        long application = (long)(sql.ExecuteScalar() ?? -1L);
        sql.CommandText = "PRAGMA user_version;";
        long version = (long)(sql.ExecuteScalar() ?? -1L);
        if (application == BinaryApplicationId &&
            version is InlineBindingLedgerVersion or DocumentPlanLedgerVersion or SourceManifestArtifactLedgerVersion or NativeEventLedgerVersion or NativeReportLedgerVersion or NativeCheckpointLedgerVersion or NativeProcessLedgerVersion or NativeProfileLedgerVersion)
        {
            binaryLedgerVersion = (int)version;
            tx?.Commit();
            return;
        }

        sql.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' LIMIT 1;";
        if (readOnly || application != 0 || version != 0 || sql.ExecuteScalar() is not null)
        {
            throw new InvalidDataException("Unsupported binary operation ledger identity or version.");
        }

        sql.CommandText = """
            CREATE TABLE workflow_binary_inputs (
                operation_id TEXT PRIMARY KEY NOT NULL REFERENCES accepted_workflows(operation_id),
                format INTEGER NOT NULL CHECK(format=1),
                document_id TEXT NOT NULL CHECK(length(document_id)=64),
                blob_id TEXT NOT NULL CHECK(length(blob_id)=64),
                canonical_digest TEXT NOT NULL CHECK(length(canonical_digest)=64),
                schema_version INTEGER NOT NULL CHECK(schema_version IN (1,2)),
                stored_length INTEGER NOT NULL CHECK(stored_length BETWEEN 16 AND 16777224),
                manifest_path TEXT NOT NULL,
                results_directory TEXT NOT NULL,
                jobs INTEGER NOT NULL CHECK(jobs>0)
            ) STRICT;
            CREATE TABLE workflow_binary_authorizations (
                operation_id TEXT PRIMARY KEY NOT NULL REFERENCES workflow_binary_inputs(operation_id),
                identity_policy INTEGER NOT NULL CHECK(identity_policy=2),
                identity TEXT NOT NULL CHECK(length(identity)=64),
                plan_document_id TEXT NOT NULL CHECK(length(plan_document_id)=64)
            ) STRICT;
            PRAGMA application_id=1129142082;
            PRAGMA user_version=9;
            """ + NativeWorkflowEventSchema + NativeWorkflowReportSchema + NativeWorkflowCheckpointSchema + NativeProcessCheckpointSchema + StableProcessIdentitySchema;
        _ = sql.ExecuteNonQuery();
        tx!.Commit();
        binaryLedgerVersion = NativeProfileLedgerVersion;
    }

    internal Task<DocumentStore> OpenExecutionDocumentsAsync(CancellationToken ct)
    {
        string root = WorkflowLedgerPath.Documents(workspace);
        return UsesNativeWorkflowReports ? DocumentStore.OpenReportStoreAsync(root, ct)
            : UsesDocumentPlans ? DocumentStore.OpenExecutionStoreAsync(root, ct)
            : DocumentStore.OpenProductionStoreAsync(root, ct);
    }

    internal async Task<StoredPlanAuthorization?> PublishExecutionPlanAsync(string operationId, byte[] binding, CancellationToken ct)
    {
        if (!UsesDocumentPlans)
        {
            return null;
        }

        DocumentStore documents = await OpenExecutionDocumentsAsync(ct);
        string identity = BinaryDispatchBinding.Identity(binding, documentPlan: true);
        DocumentDescriptor plan = await documents.ImportExecutionPlanAsync(binding,
            "execution-plan/" + operationId + "/" + identity, ct);
        return new(plan.Id, identity);
    }

    internal async Task<(StoredPlanAuthorization? Authorization, DocumentSession? Plan)> VerifyExecutionPlanAsync(
        string operationId, byte[]? binding, CancellationToken ct)
    {
        if (!UsesDocumentPlans)
        {
            RequireBinaryDispatch(operationId, binding, null);
            return (null, null);
        }

        StoredPlanAuthorization authorization = ReadPlanAuthorization(operationId, null);
        DocumentStore documents = await OpenExecutionDocumentsAsync(ct);
        DocumentSession plan = await documents.OpenAsync(authorization.Document, ct: ct);
        if (plan.Descriptor.Codec != DocumentCodec.ExecutionPlanCapnp || !plan.Verification.SchemaProjection ||
            !plan.InspectStoredBytes(bytes => (binding is null || bytes.SequenceEqual(binding)) &&
                authorization.Identity == BinaryDispatchBinding.Identity(bytes, documentPlan: true)))
        {
            plan.Dispose();
            throw new InvalidDataException("Resolved execution plan differs from the authorized document.");
        }

        return (authorization, plan);
    }

    internal async Task VerifyStoredDispatchAsync(string operationId, CancellationToken ct)
    {
        (_, DocumentSession? plan) = await VerifyExecutionPlanAsync(operationId, null, ct);
        plan?.Dispose();
    }

    internal BinaryWorkflowInput? ReadBinaryInput(string operationId, SqliteTransaction? tx = null)
    {
        if (!BinaryLedger)
        {
            return null;
        }

        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            SELECT format,document_id,blob_id,canonical_digest,schema_version,stored_length,manifest_path,results_directory,jobs
            FROM workflow_binary_inputs WHERE operation_id=$operation;
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        using SqliteDataReader row = sql.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }

        if (row.GetInt32(0) != 1)
        {
            throw new InvalidDataException("Unsupported binary workflow input format.");
        }

        DocumentDescriptor descriptor = new(new(row.GetString(1)), DocumentCodec.ProductionWorkflowCapnp, row.GetInt32(4),
            new(row.GetString(2)), new CanonicalDigest(row.GetString(3)), row.GetInt64(5));
        return new(descriptor, row.GetString(6), row.GetString(7), row.GetInt32(8));
    }

    private void InsertBinaryInput(SqliteTransaction tx, string operationId, BinaryWorkflowInput input)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            INSERT INTO workflow_binary_inputs(operation_id,format,document_id,blob_id,canonical_digest,schema_version,
                stored_length,manifest_path,results_directory,jobs)
            VALUES($operation,1,$document,$blob,$canonical,$schema,$length,$manifest,$results,$jobs);
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        _ = sql.Parameters.AddWithValue("$document", input.Document.Id.Value);
        _ = sql.Parameters.AddWithValue("$blob", input.Document.Blob.Value);
        _ = sql.Parameters.AddWithValue("$canonical", input.Document.Canonical!.Value.Value);
        _ = sql.Parameters.AddWithValue("$schema", input.Document.SchemaVersion);
        _ = sql.Parameters.AddWithValue("$length", input.Document.StoredLength);
        _ = sql.Parameters.AddWithValue("$manifest", input.ManifestPath);
        _ = sql.Parameters.AddWithValue("$results", input.ResultsDirectory);
        _ = sql.Parameters.AddWithValue("$jobs", input.Jobs);
        _ = sql.ExecuteNonQuery();
    }

    private void BindBinaryDispatch(SqliteTransaction tx, string operationId, byte[] plan,
        StoredPlanAuthorization? authorization, bool initial)
    {
        if (initial)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = tx;
            _ = insert.Parameters.AddWithValue("$operation", operationId);
            if (UsesDocumentPlans)
            {
                string identity = BinaryDispatchBinding.Identity(plan, documentPlan: true);
                if (authorization is null || authorization.Identity != identity)
                {
                    throw new InvalidDataException("A retained execution plan must precede dispatch authorization.");
                }

                insert.CommandText = """
                    INSERT INTO workflow_binary_authorizations(operation_id,identity_policy,identity,plan_document_id)
                    VALUES($operation,2,$identity,$document);
                    """;
                _ = insert.Parameters.AddWithValue("$identity", identity);
                _ = insert.Parameters.AddWithValue("$document", authorization.Document.Value);
            }
            else
            {
                insert.CommandText = """
                    INSERT INTO workflow_binary_authorizations(operation_id,identity_policy,identity,binding)
                    VALUES($operation,1,$identity,$binding);
                    """;
                _ = insert.Parameters.AddWithValue("$identity", BinaryDispatchBinding.Identity(plan));
                insert.Parameters.Add("$binding", SqliteType.Blob).Value = plan;
            }
            _ = insert.ExecuteNonQuery();
        }
        else
        {
            RequireBinaryDispatch(operationId, plan, authorization, tx);
        }
    }

    private StoredPlanAuthorization ReadPlanAuthorization(string operationId, SqliteTransaction? tx)
    {
        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            SELECT identity_policy,identity,plan_document_id FROM workflow_binary_authorizations WHERE operation_id=$operation;
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        using SqliteDataReader row = sql.ExecuteReader();
        return row.Read() && row.GetInt32(0) == 2 && ArtifactStoreFiles.IsCanonicalHex(row.GetString(1), 64) &&
            ArtifactStoreFiles.IsCanonicalHex(row.GetString(2), 64)
            ? new(new(row.GetString(2)), row.GetString(1))
            : throw new InvalidDataException("Missing or unsupported execution plan authorization.");
    }

    private void RequireBinaryDispatch(string operationId, byte[]? expected,
        StoredPlanAuthorization? authorization, SqliteTransaction? tx = null)
    {
        if (UsesDocumentPlans)
        {
            if (authorization is null || ReadPlanAuthorization(operationId, tx) != authorization)
            {
                throw new InvalidDataException("Execution plan authorization changed before dispatch.");
            }

            return;
        }

        using SqliteCommand sql = connection.CreateCommand();
        sql.Transaction = tx;
        sql.CommandText = """
            SELECT identity_policy,identity,binding FROM workflow_binary_authorizations WHERE operation_id=$operation;
            """;
        _ = sql.Parameters.AddWithValue("$operation", operationId);
        using SqliteDataReader row = sql.ExecuteReader();
        if (!row.Read() || row.GetInt32(0) != 1)
        {
            throw new InvalidDataException("Missing or unsupported binary dispatch binding.");
        }

        byte[] committed = (byte[])row.GetValue(2);
        if (BinaryDispatchBinding.Identity(committed) != row.GetString(1) ||
            (expected is not null && !committed.AsSpan().SequenceEqual(expected)))
        {
            throw new InvalidDataException("Resolved binary dispatch plan differs from the authorized binding.");
        }
    }

    internal async Task<AcceptedWorkflow> AcceptBinaryAsync(ExecutionStartRequest request, CancellationToken ct)
    {
        if (!BinaryLedger)
        {
            throw new InvalidOperationException("binary_workspace_not_initialized");
        }

        if (string.IsNullOrWhiteSpace(request.ClientRequestId) || request.ClientRequestId.Length > 128 ||
            request.Jobs < 1 || !IsSha256(request.ManifestSha256) ||
            (request.SourceSnapshotId is { } submittedSource && !IsSha256(submittedSource)))
        {
            throw new ArgumentException("Invalid binary workflow admission request.", nameof(request));
        }

        WorkspaceReadPaths paths = new(workspace);
        string source = paths.Require(request.Manifest);
        string results = paths.Require(request.ResultsDirectory);
        if (WorkflowLedgerPath.InBinaryMetadata(workspace, source) || WorkflowLedgerPath.InBinaryMetadata(workspace, results))
        {
            throw new ArgumentException("Submission and results paths must be outside execution metadata.", nameof(request));
        }

        BlobId expected = new(request.ManifestSha256);
        string[] selection = WorkflowNodeSelection.Normalize(request.Only);
        string? selectionJson = selection.Length == 0 ? null : JsonSerializer.Serialize(selection, HostJson.Default.StringArray);
        (HistoryPolicyMode historyMode, string? historyDatabase) = WorkflowHistoryPolicy.Normalize(request.HistoryPolicy, paths);
        string? snapshot = request.SourceSnapshotId?.ToUpperInvariant();
        if (snapshot is not null && !UsesDocumentPlans)
        {
            throw new InvalidOperationException("native_source_requires_fresh_execution_workspace");
        }
        string requestDigest = BinaryRequestDigest(source, results, request.Jobs, expected, selectionJson, historyMode,
            historyDatabase, snapshot);
        if (Find(request.ClientRequestId) is { } existing)
        {
            return existing.RequestDigest == requestDigest && ReadBinaryInput(existing.OperationId) is not null
                ? existing
                : throw new InvalidOperationException("request_id_conflict");
        }

        await using FileStream file = OperationArtifactFile.OpenRead(workspace, source);
        if (file.Length is < 16 or > DocumentStore.MaximumStoredDocumentBytes)
        {
            throw new InvalidDataException("Binary workflow frame is outside the supported size range.");
        }

        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, ct);
        if (file.ReadByte() != -1 || !string.Equals(Hex(bytes), expected.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("stale_manifest");
        }

        DocumentStore documents = await OpenExecutionDocumentsAsync(ct);
        DocumentDescriptor descriptor = await documents.ImportBinaryRetainedAsync(bytes, "execution-input/" + expected.Value, ct);
        using DocumentSession session = await documents.OpenAsync(descriptor.Id, ct: ct);
        WorkflowDocument workflow = session.Materialize();
        if (snapshot is null)
        {
            RequireDispatchablePlan(await WorkflowPlanner.LoadDocumentAsync(source, workflow, Hex(bytes), selection, null, ct, paths.Read));
        }
        else
        {
            await ValidateBinarySourceAsync(paths, source, workflow, selection, snapshot, ct);
        }
        ct.ThrowIfCancellationRequested();
        return AcceptImmutableCanonical(new(request.ClientRequestId, source, expected.Value, results, requestDigest,
            WorkflowSelection: selectionJson, HistoryDatabase: historyDatabase, HistoryMode: historyMode,
            SourceSnapshotId: snapshot), new(descriptor, source, results, request.Jobs));
    }

    private async Task ValidateBinarySourceAsync(WorkspaceReadPaths paths, string manifest, WorkflowDocument workflow,
        IReadOnlyList<string> selection, string sourceId, CancellationToken ct)
    {
        string source = WorkflowSourceSnapshotExecution.ValidateSourceLayout(paths, manifest, workflow, AllowsSourceManifestArtifacts);
        _ = WorkflowPlanner.ValidateGraph(workflow, selection);
        LoreSnapshotStore store = new(SourceStorePaths.ForWorkspace(paths.WorkspaceDirectory));
        SnapshotManifest snapshot = await store.VerifyAsync(sourceId, ct);
        if (!snapshot.Files.Any(file => file.Path == "CMakeLists.txt"))
        {
            throw new InvalidDataException("Source snapshot does not contain CMakeLists.txt at its root.");
        }
        if (AllowsSourceManifestArtifacts)
        {
            _ = SourceBoundArtifactLayout.Validate(workflow, manifest, source, snapshot);
        }

        foreach (WorkflowContext context in workflow.Contexts.Values)
        {
            if (context.Toolchain is not { VcpkgProjectSha256: { } dependency } binding)
            {
                continue;
            }

            await WorkflowSourceSnapshotExecution.VerifyExistingMaterializationAsync(snapshot, source, ct);
            string environment = await VcpkgProjects.VerifySnapshotInstallationAsync(dependency, sourceId, source, ct);
            if (binding.EnvironmentLockSha256 != environment || binding.EnvironmentProfileId != "linux-managed-clang" ||
                binding.HostArchitecture != "x64" || binding.TargetArchitecture != "x64" ||
                binding.Generator is not ("Ninja" or "Ninja Multi-Config"))
            {
                throw new InvalidDataException("Workflow vcpkg binding differs from the approved managed environment.");
            }
        }
    }

    internal static void RequireDispatchablePlan(WorkflowPlan plan)
    {
        string[] errors = [.. plan.Nodes.SelectMany(n => n.Diagnostics.Where(d => d.Severity == "Error")
            .Select(d => $"{n.Definition.Id}: {d.Message}"))];
        if (errors.Length != 0)
        {
            throw new InvalidOperationException("workflow_preflight_rejected: " + string.Join(" | ", errors));
        }
    }

    private static string BinaryRequestDigest(string manifest, string results, int jobs, BlobId blob,
        string? selection, HistoryPolicyMode historyMode, string? historyDatabase, string? sourceSnapshot)
    {
        using MemoryStream preimage = new();
        using BinaryWriter writer = new(preimage, Encoding.UTF8, leaveOpen: true);
        writer.Write(sourceSnapshot is null ? "cmwf-workflow/start:capnp-v1" : "cmwf-workflow/start:capnp-source-v1");
        writer.Write(manifest);
        writer.Write(results);
        writer.Write(jobs);
        writer.Write(blob.Value);
        writer.Write(selection ?? "");
        writer.Write((int)historyMode);
        writer.Write(historyDatabase ?? "");
        if (sourceSnapshot is not null)
        {
            writer.Write(sourceSnapshot);
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(preimage.GetBuffer().AsSpan(0, (int)preimage.Length)));
    }
}
