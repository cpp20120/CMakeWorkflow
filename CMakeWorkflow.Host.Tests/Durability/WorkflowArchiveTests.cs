using System.Diagnostics;
using System.Security.Cryptography;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Storage.Documents;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static class WorkflowArchiveTests
{
    internal static async Task RunAsync(string? cli, string? oldCli)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));
        CancellationToken ct = deadline.Token;
        DirectoryInfo temp = Directory.CreateTempSubdirectory("cmwf-workflow-archive-");
        try
        {
            // macOS temporary directories may be reached through /var -> /private/var.
            // Keep the store's no-symlink policy; use the canonical fixture root.
            string root = CMakeWorkflow.CMake.Build.BuildTreeOwnership.CanonicalPath(temp.FullName);
            string workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            string casPath = Path.Combine(root, "cas");
            string manifest = Path.Combine(workspace, "external.json");
            byte[] original = """
                {
                    "schemaVersion": 2,
                    "contexts": {"native": {"buildDirectory": "build"}},
                    "nodes": [{"id":"inspect","kind":"inspect","context":"native"}]
                }
                """u8.ToArray();
            string digest = Convert.ToHexStringLower(SHA256.HashData(original));
            await File.WriteAllBytesAsync(manifest, original, ct);

            AcceptedWorkflow accepted;
            using (DurableAcceptanceStore ledger = new(workspace))
            {
                accepted = ledger.Accept("archive-legacy-request", manifest, digest, 1, workspace);
            }

            string ledgerPath = Path.Combine(workspace, ".cmakeworkflow", "operations.sqlite");
            string backupPath = Path.Combine(temp.FullName, "legacy-backup.sqlite");
            File.Copy(ledgerPath, backupPath);
            await File.WriteAllTextAsync(manifest, "edited after acceptance", ct);
            if (cli is not null)
            {
                await RequireCommandAsync(cli, ct, "workflow", "archive", workspace, accepted.OperationId, casPath);
            }

            MigrationResult archived = await WorkflowArchive.ArchiveAcceptedAsync(workspace, accepted.OperationId, casPath, ct);
            Require(archived.Legacy.Legacy?.Value == digest && archived.Binary.Codec == DocumentCodec.ProductionWorkflowCapnp);
            Require(archived == await WorkflowArchive.ArchiveAcceptedAsync(workspace, accepted.OperationId, casPath, ct));

            DocumentStore store = await DocumentStore.OpenProductionStoreAsync(casPath, ct);
            string reference = $"workflow/{accepted.WorkspaceId}/{accepted.OperationId}";
            Require(await store.ResolveReferenceAsync(reference + "/legacy", ct) == archived.Legacy
                && await store.ResolveReferenceAsync(reference + "/binary", ct) == archived.Binary);
            using (DocumentSession session = await store.OpenAsync(archived.Legacy.Id, ct: ct))
            {
                Require(session.ExportStoredBytes().AsSpan().SequenceEqual(original));
            }

            using (DocumentSession session = await store.OpenAsync(archived.Binary.Id, DocumentAccess.Mapped, ct: ct))
            {
                Require(session.Verification.CanonicalSegment && session.Materialize().Nodes[0].Context == "native");
            }

            if (cli is not null)
            {
                await CheckCombinedCollectionAsync(cli, oldCli, casPath, archived, store, temp.FullName, ct);
            }

            byte[] originalLedger = await File.ReadAllBytesAsync(backupPath, ct);
            byte[] archivedLedger = await File.ReadAllBytesAsync(ledgerPath, ct);
            Require(archivedLedger.AsSpan().SequenceEqual(originalLedger));
            string restore = Directory.CreateDirectory(Path.Combine(temp.FullName, "restore")).FullName;
            _ = Directory.CreateDirectory(Path.Combine(restore, ".cmakeworkflow"));
            File.Copy(backupPath, Path.Combine(restore, ".cmakeworkflow", "operations.sqlite"));
            using DurableAcceptanceStore restored = new(restore, readOnly: true);
            Require(restored.Get(accepted.OperationId) == accepted
                && restored.GetSnapshot(accepted.OperationId).AsSpan().SequenceEqual(original));
            Console.WriteLine("Accepted workflow archive, immutable retry and ledger restore passed.");
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static async Task CheckCombinedCollectionAsync(string cli, string? oldCli, string casPath, MigrationResult archived,
        DocumentStore store, string temp, CancellationToken ct)
    {
        WorkflowDocument doc = new()
        {
            SchemaVersion = 1,
            Nodes = [new() { Id = "action-output", Kind = "inspect" }]
        };
        DocumentDescriptor fromAction = await store.PublishAsync(doc, ct);
        doc.Nodes[0].Id = "live-mapping";
        DocumentDescriptor active = await store.PublishAsync(doc, ct);
        doc.Nodes[0].Id = "unrooted";
        DocumentDescriptor unrooted = await store.PublishAsync(doc, ct);

        ArtifactStoreFiles files = new(casPath);
        string actionKey = new('a', 64);
        CasActions actions = new(files);
        await actions.PublishAsync(actionKey, files.Blob(fromAction.Blob.Value), fromAction.Blob.Value, ct);

        if (oldCli is not null)
        {
            (int status, string output) = await InvokeAsync(oldCli, ct, "cas", casPath, "gc", "--apply");
            Require(status != 0 && output.Contains("Unrecognized CMakeWorkflow artifact CAS format", StringComparison.Ordinal));
        }

        using (DocumentSession mapping = await store.OpenAsync(active.Id, DocumentAccess.Mapped, ct: ct))
        {
            await RequireCommandAsync(cli, ct, "cas", casPath, "gc");
            Require(await store.FindAsync(unrooted.Id, ct) is not null);
            await RequireCommandAsync(cli, ct, "cas", casPath, "gc", "--apply");
            Require(await store.FindAsync(unrooted.Id, ct) is null
                && File.Exists(files.Blob(active.Blob.Value)) && File.Exists(files.Blob(archived.Binary.Blob.Value))
                && mapping.Materialize().Nodes[0].Id == "live-mapping");
            string fetched = Path.Combine(temp, "restored-action");
            Require(await actions.TryRestoreAsync(actionKey, fetched, ct));
        }

        await actions.DropAsync(actionKey, ct);
        await RequireCommandAsync(cli, ct, "cas", casPath, "gc", "--apply");
        Require(!File.Exists(files.Blob(active.Blob.Value)) && !File.Exists(files.Blob(fromAction.Blob.Value))
            && File.Exists(files.Blob(archived.Legacy.Blob.Value)));
        Console.WriteLine("Native CLI GC protected archive references, action cache roots and cross-process mapped leases.");
    }

    private static async Task RequireCommandAsync(string cli, CancellationToken ct, params string[] args)
    {
        (int status, string output) = await InvokeAsync(cli, ct, args);
        if (status != 0)
        {
            throw new InvalidOperationException(output);
        }
    }

    private static async Task<(int, string)> InvokeAsync(string cli, CancellationToken ct, params string[] args)
    {
        ProcessStartInfo start = new(Path.GetFullPath(cli)) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process child = Process.Start(start) ?? throw new InvalidOperationException("CLI process not started.");
        Task<string> output = child.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = child.StandardError.ReadToEndAsync(ct);
        await child.WaitForExitAsync(ct);
        return (child.ExitCode, await output + await error);
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException("Workflow archival or coordinated collection invariant failed.");
        }
    }
}
