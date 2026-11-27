using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Sources;
using CMakeWorkflow.Tooling.Tools;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host.Workflows;

/// <summary>Observed format and path inventory for a stopped or live workspace.</summary>
public sealed record WorkflowStorageFormats(string Workspace, string OperationLedger, string? OperationLedgerFormat,
    string DocumentStore, string? ArtifactStoreFormat, long? DocumentCatalogVersion, string SourceStore, string ToolStore)
{
    /// <summary>Reads persisted format markers without creating a workspace or converting any data.</summary>
    public static async Task<WorkflowStorageFormats> InspectAsync(string workspace, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace))
        {
            throw new ArgumentException("An existing absolute workspace directory is required.", nameof(workspace));
        }

        WorkspaceReadPaths paths = new(workspace);
        string root = paths.WorkspaceDirectory;
        string ledgerPath = WorkflowLedgerPath.Resolve(root);
        string? ledgerFormat = null;
        if (File.Exists(ledgerPath))
        {
            _ = paths.Database(ledgerPath);
            using DurableAcceptanceStore ledger = new(root, readOnly: true);
            ledgerFormat = ledger.NativeFormatEpoch is { } epoch
                ? FormattableString.Invariant($"binary-{epoch}") : "legacy-sqlite";
        }

        string documentRoot = paths.Require(WorkflowLedgerPath.Documents(root));
        string marker = paths.Require(Path.Combine(documentRoot, ".cas-format"));
        if (!File.Exists(marker))
        {
            return new(root, ledgerPath, ledgerFormat, documentRoot, null, null,
                SourceStorePaths.ForWorkspace(root), ManagedToolStore.GetRoot());
        }
        if (new FileInfo(marker).Length is < 1 or > 64)
        {
            throw new InvalidDataException("Invalid execution CAS format marker.");
        }

        string format = await File.ReadAllTextAsync(marker, ct);
        if (format is not ("cmakeworkflow-artifact-cas-v2" or "cmakeworkflow-artifact-cas-v3"))
        {
            throw new InvalidDataException("Unsupported execution CAS format marker.");
        }

        string catalogPath = paths.Database(Path.Combine(documentRoot, "documents.sqlite"));
        SQLitePCL.Batteries.Init();
        await using SqliteConnection sql = new(new SqliteConnectionStringBuilder
        {
            DataSource = catalogPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await sql.OpenAsync(ct);
        await using SqliteCommand check = sql.CreateCommand();
        check.CommandText = "SELECT application_id, user_version FROM pragma_application_id, pragma_user_version;";
        await using SqliteDataReader row = await check.ExecuteReaderAsync(ct);
        return await row.ReadAsync(ct) && row.GetInt32(0) == 0x434D5744 && row.GetInt64(1) is 2 or 3
            ? new(root, ledgerPath, ledgerFormat, documentRoot, format, row.GetInt64(1),
                SourceStorePaths.ForWorkspace(root), ManagedToolStore.GetRoot())
            : throw new InvalidDataException("Unsupported execution document catalog.");
    }
}
