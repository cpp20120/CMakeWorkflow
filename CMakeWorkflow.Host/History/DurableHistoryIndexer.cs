using System.Text.Json;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.History.Storage;
using CMakeWorkflow.History.Models;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Workflows;
using Microsoft.Data.Sqlite;

namespace CMakeWorkflow.Host.History;

internal static class DurableHistoryIndexer
{
    private static readonly string[] SidecarSuffixes = ["-journal", "-wal", "-shm"];

    internal static DurableAcceptanceStore OpenReadOnly(WorkspaceReadPaths paths)
    {
        _ = paths.Database(LedgerPath(paths));
        return new(paths.WorkspaceDirectory, readOnly: true);
    }

    internal static string LedgerPath(WorkspaceReadPaths paths) =>
        WorkflowLedgerPath.Resolve(paths.WorkspaceDirectory);

    internal static string AuthorizeDatabase(WorkspaceReadPaths paths, string database)
    {
        string resolved = paths.DatabaseForWrite(database);
        string ledger = paths.Require(LedgerPath(paths));
        return WorkflowPlanner.PathComparer.Equals(resolved, ledger) ||
            SidecarSuffixes.Any(suffix => WorkflowPlanner.PathComparer.Equals(resolved, ledger + suffix)) ||
            WorkflowLedgerPath.InBinaryMetadata(paths.WorkspaceDirectory, resolved)
            ? throw new ApplicationRefusal("invalid_history_policy", "History cannot use the operation ledger or execution document store.")
            : resolved;
    }

    internal static async Task RecoverAsync(WorkspaceReadPaths paths, CancellationToken ct)
    {
        if (!File.Exists(paths.Require(LedgerPath(paths))))
        {
            return;
        }

        IReadOnlyList<string> operations;
        using (DurableAcceptanceStore reader = OpenReadOnly(paths))
        {
            operations = reader.PendingHistoryOperations();
        }

        foreach (string operationId in operations)
        {
            _ = await IndexAsync(paths, operationId, retry: false, ct);
        }
    }

    internal static Task<HistoryIndexStatus> IndexAsync(WorkspaceReadPaths paths, string operationId,
        bool retry, CancellationToken ct) => IndexAsync(paths, operationId, retry, null, ct);

    internal static async Task<HistoryIndexStatus> IndexAsync(WorkspaceReadPaths paths, string operationId,
        bool retry, Action? afterCommit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(operationId, "N", out _))
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        HistoryIndexStatus status;
        using (DurableAcceptanceStore reader = OpenReadOnly(paths))
        {
            status = reader.GetHistoryIndexStatus(operationId);
        }

        if (!IsEligible(status.State, retry))
        {
            return status;
        }

        string leaseDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "history-locks"));
        _ = Directory.CreateDirectory(leaseDirectory);
        string leasePath = paths.Require(Path.Combine(leaseDirectory, operationId + ".lock"));
        using FileStream? lease = TryLease(leasePath);
        if (lease is null)
        {
            return status;
        }

        _ = paths.Database(LedgerPath(paths));
        using DurableAcceptanceStore ledger = new(paths.WorkspaceDirectory);
        status = ledger.GetHistoryIndexStatus(operationId);
        string? attemptId = ledger.ClaimHistoryIndex(operationId, retry);
        if (attemptId is null)
        {
            return status;
        }

        DurableAcceptanceStore.Finalization finalization = ledger.GetFinalization(operationId)
            ?? throw new InvalidOperationException("history_finalization_missing");
        HistoryImportResult imported;
        NativeReportReference? native = null;
        try
        {
            string database = AuthorizeDatabase(paths, status.DatabasePath!);
            if (!WorkflowPlanner.PathComparer.Equals(database, status.DatabasePath!))
            {
                throw new ApplicationRefusal("history_database_changed", "The admitted History database path changed.");
            }

            // Verify the terminal evidence before reading supplemental profiling data.
            native = ledger.ReadNativeReportReference(operationId);
            NativeExecutionReport? nativeReport = null;
            if (native is null)
            {
                _ = await OperationArtifactReader.ReportAsync(paths, operationId, ct);
            }
            else
            {
                AcceptedWorkflow accepted = ledger.Get(operationId)
                    ?? throw new InvalidDataException("History report has no accepted workflow.");
                nativeReport = await ledger.ReadNativeReportAsync(accepted, native, ct);
                if (nativeReport.ExitCode != finalization.ExitCode || nativeReport.Report.Status != finalization.State ||
                    nativeReport.RunDirectory != finalization.RunDirectory)
                {
                    throw new InvalidDataException("Native History evidence differs from finalization.");
                }
            }
            string run = paths.Require(finalization.RunDirectory);
            if (finalization.State == "succeeded" &&
                Directory.EnumerateFiles(run, "*.results.json").Any() &&
                !File.Exists(paths.Require(Path.Combine(run, "analysis.json"))))
            {
                _ = await DoctorSession.AnalyzeProfileAsync(paths, run, ct);
            }

            ct.ThrowIfCancellationRequested();
            using HistoryStore history = new(database);
            imported = nativeReport is not null && native is not null
                ? await history.ImportNativeWorkflowAsync(nativeReport,
                    new(operationId, native.Document.Value, native.Blob.Value, native.SchemaVersion), GuardPath, ct)
                : await history.ImportFinalizedWorkflowAsync(run, finalization.ReportSha256, GuardPath, ct);

            void GuardPath(string file)
            {
                ct.ThrowIfCancellationRequested();
                _ = paths.Require(file);
            }
        }
        catch (Exception error) when (error is ApplicationRefusal or IOException or UnauthorizedAccessException
                                          or SqliteException or JsonException or ArgumentException
                                          or InvalidOperationException or InvalidDataException or FormatException
                                          or System.ComponentModel.Win32Exception or OverflowException or NotSupportedException
                                          or KeyNotFoundException or System.Xml.XmlException)
        {
            string code = error switch
            {
                ApplicationRefusal refusal => refusal.Code,
                SqliteException => "history_database_error",
                InvalidDataException or JsonException or KeyNotFoundException or System.Xml.XmlException => "history_source_invalid",
                UnauthorizedAccessException => "history_access_denied",
                _ => "history_index_failed"
            };
            _ = ledger.CompleteHistoryIndex(operationId, attemptId, code);
            return ledger.GetHistoryIndexStatus(operationId);
        }

        // Keep acknowledgement outside the import catch. If it fails after
        // the History commit, the next owner re-imports the same verified run.
        afterCommit?.Invoke();
        if (native is not null)
        {
            await RecoveryFaults.HitAsync("after-native-history-commit", null, ct);
        }
        _ = ledger.CompleteHistoryIndex(operationId, attemptId, runs: imported.RunIdentities);
        return ledger.GetHistoryIndexStatus(operationId);
    }

    private static bool IsEligible(HistoryIndexState state, bool retry) =>
        state is HistoryIndexState.Pending or HistoryIndexState.Indexing || (retry && state == HistoryIndexState.Failed);

    private static FileStream? TryLease(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // The competing worker owns the same persistent operation lease.
            return null;
        }
    }
}
