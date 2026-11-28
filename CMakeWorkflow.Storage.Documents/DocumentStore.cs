using Microsoft.Data.Sqlite;
using Capnp.Managed;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.CMake.Reporting;

namespace CMakeWorkflow.Storage.Documents;

internal enum StoreFault
{
    BeforeStage, DuringStage, AfterWrite, AfterFlush, BeforePublish, AfterPublish,
    BeforeCommit, AfterCommit, BeforeReturn, AfterPrune, BeforeReferenceCommit
}

/// <summary>Local workflow catalog and immutable CAS-backed document store.</summary>
public sealed class DocumentStore
{
    private readonly ArtifactStoreFiles files;
    private readonly string database;
    private readonly bool production;
    private readonly int catalogVersion;
    private readonly bool requireIndexedActions;
    private readonly Action<StoreFault>? fault;

    private DocumentStore(string root, int catalogVersion, Action<StoreFault>? fault = null, bool requireIndexedActions = false)
    {
        Root = Path.GetFullPath(root);
        production = catalogVersion != Catalog.PrototypeVersion;
        this.catalogVersion = catalogVersion;
        this.requireIndexedActions = requireIndexedActions;
        files = new(production ? Root : Path.Combine(Root, "payload"));
        database = Path.Combine(Root, ArtifactStoreFiles.DocumentCatalogName);
        this.fault = fault;
    }

    public string Root { get; }
    public const int MaximumStoredDocumentBytes = WorkflowDocuments.MaxStoredBytes;

    public static Task<DocumentStore> OpenStoreAsync(string path, CancellationToken ct = default) =>
        InitializeAsync(path, ct);

    /// <summary>Opens or creates a shared, document-aware CAS with catalog schema version 2.</summary>
    public static Task<DocumentStore> OpenProductionStoreAsync(string path, CancellationToken ct = default) =>
        InitializeAsync(path, ct, production: true);
    internal static DocumentStore OpenExistingProductionStore(string path) => new(path, Catalog.WorkflowVersion);

    /// <summary>Opens or creates the fenced execution catalog with retained plans.</summary>
    public static Task<DocumentStore> OpenExecutionStoreAsync(string path, CancellationToken ct = default) =>
        InitializeAsync(path, Catalog.ExecutionVersion, null, indexedActions: false, ct);

    /// <summary>Opens or creates a fresh execution catalog that supports native terminal reports.</summary>
    public static Task<DocumentStore> OpenReportStoreAsync(string path, CancellationToken ct = default) =>
        InitializeAsync(path, Catalog.ReportVersion, null, indexedActions: false, ct);

    /// <summary>Accesses a report catalog without creating missing storage metadata.</summary>
    public static DocumentStore OpenExistingReportStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new(path, Catalog.ReportVersion);
    }

    internal static Task<DocumentStore> InitializeIndexedActionsAsync(string path, CancellationToken ct) =>
        InitializeAsync(path, Catalog.ExecutionVersion, null, indexedActions: true, ct);

    internal static Task<DocumentStore> InitializeAsync(string path, CancellationToken ct,
        Action<StoreFault>? fault = null, bool production = false) =>
        InitializeAsync(path, production ? Catalog.WorkflowVersion : Catalog.PrototypeVersion, fault, indexedActions: false, ct);

    private static async Task<DocumentStore> InitializeAsync(string path, int catalogVersion, Action<StoreFault>? fault,
        bool indexedActions, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        DocumentStore store = new(path, catalogVersion, fault, requireIndexedActions: indexedActions);
        ct.ThrowIfCancellationRequested();
        CheckAncestors(store.Root);
        _ = OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(store.Root)
            : Directory.CreateDirectory(store.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        if (!store.production && File.Exists(Path.Combine(store.Root, ".cas-format")))
        {
            throw new InvalidDataException("Choose a directory outside the existing artifact CAS.");
        }

        using FileStream lease = await store.AcquireAsync(create: true, ct).ConfigureAwait(false);
        using Catalog catalog = store.OpenCatalog(initialize: true);
        if (indexedActions)
        {
            using CasReferenceIndex index = new(store.files, initialize: true);
        }
        return store;
    }

    /// <summary>Commits an exact canonical execution plan and its retention reference.</summary>
    public Task<DocumentDescriptor> ImportExecutionPlanAsync(ReadOnlySpan<byte> officialFrame, string reference,
        CancellationToken ct = default)
    {
        if (catalogVersion < Catalog.ExecutionVersion)
        {
            throw new InvalidOperationException("Execution plans require the fenced execution catalog.");
        }

        RequireName(reference);
        ct.ThrowIfCancellationRequested();
        return PublishAsync(WorkflowDocuments.ImportExecutionPlan(officialFrame), ct, reference);
    }

    /// <summary>Publishes a terminal report and an immutable operation retention reference.</summary>
    public Task<DocumentDescriptor> PublishExecutionReportAsync(NativeExecutionReport report, string reference,
        CancellationToken ct = default)
    {
        if (catalogVersion != Catalog.ReportVersion)
        {
            throw new InvalidOperationException("Execution reports require the fenced report catalog.");
        }

        RequireName(reference);
        ct.ThrowIfCancellationRequested();
        return PublishAsync(WorkflowDocuments.EncodeExecutionReport(report), ct, reference, immutableReference: true);
    }

    public Task<DocumentDescriptor> PublishAsync(WorkflowDocument workflow, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return PublishAsync(WorkflowDocuments.Encode(workflow, production), ct);
    }

    /// <summary>Publishes a workflow and establishes its retention reference in the same catalog transaction.</summary>
    public Task<DocumentDescriptor> PublishRetainedAsync(WorkflowDocument workflow, string reference, CancellationToken ct = default)
    {
        RequireName(reference);
        ct.ThrowIfCancellationRequested();
        return PublishAsync(WorkflowDocuments.Encode(workflow, production), ct, reference);
    }

    public Task<DocumentDescriptor> ImportJsonAsync(ReadOnlySpan<byte> json, LegacyDigest expected,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return PublishAsync(WorkflowDocuments.ImportJson(json, expected), ct);
    }

    public Task<DocumentDescriptor> ImportBinaryAsync(ReadOnlySpan<byte> officialFrame, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return PublishAsync(WorkflowDocuments.ImportBinary(officialFrame, production), ct);
    }

    /// <summary>Imports an exact binary frame and commits its retention reference with the descriptor.</summary>
    public Task<DocumentDescriptor> ImportBinaryRetainedAsync(ReadOnlySpan<byte> officialFrame, string reference,
        CancellationToken ct = default)
    {
        RequireName(reference);
        ct.ThrowIfCancellationRequested();
        return PublishAsync(WorkflowDocuments.ImportBinary(officialFrame, production), ct, reference);
    }

    private async Task<DocumentDescriptor> PublishAsync(EncodedDocument document, CancellationToken ct, string? reference = null,
        bool immutableReference = false)
    {
        using FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false);
        using Catalog catalog = OpenCatalog();
        DocumentDescriptor? prior = catalog.Find(document.Descriptor.Id);
        if (prior is not null && prior != document.Descriptor)
        {
            throw new InvalidDataException("Conflicting descriptor for an existing document identity.");
        }

        await PublishBlobAsync(document, ct).ConfigureAwait(false);
        using SqliteTransaction transaction = catalog.Begin();
        catalog.Insert(document.Descriptor, transaction);
        if (reference is not null)
        {
            if (immutableReference)
            {
                catalog.Archive(reference, document.Descriptor.Id, transaction);
            }
            else
            {
                catalog.Retain(1, reference, document.Descriptor.Id, transaction);
            }
        }

        await CommitAsync(transaction, ct).ConfigureAwait(false);
        fault?.Invoke(StoreFault.BeforeReturn);
        return document.Descriptor;
    }

    public async Task<DocumentSession> OpenAsync(DocumentId id, DocumentAccess access = DocumentAccess.Buffered,
        VerificationMode verification = VerificationMode.Full, CancellationToken ct = default)
    {
        RequireId(id);
        if (access is not (DocumentAccess.Buffered or DocumentAccess.Mapped))
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        if (verification is not (VerificationMode.Full or VerificationMode.StoredBytes))
        {
            throw new ArgumentOutOfRangeException(nameof(verification));
        }

        DocumentDescriptor descriptor;
        FileStream blob;
        using (FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false))
        {
            using Catalog catalog = OpenCatalog();
            descriptor = catalog.Find(id) ?? throw new KeyNotFoundException("Workflow document is absent.");
            if (access == DocumentAccess.Mapped && descriptor.Codec == DocumentCodec.WorkflowJson)
            {
                throw new InvalidOperationException("Legacy JSON uses buffered inspection.");
            }

            string path = BlobPath(descriptor.Blob);
            CheckPath(path);
            blob = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        }

        MappedMessage? mapping = null;
        try
        {
            if (blob.Length != descriptor.StoredLength)
            {
                throw new InvalidDataException("Physical blob has a different stored length.");
            }

            byte[]? bytes = null;
            if (access == DocumentAccess.Mapped)
            {
                mapping = MappedMessage.Open(blob, WorkflowDocuments.ViewPolicy);
            }
            else
            {
                bytes = new byte[(int)descriptor.StoredLength];
                await blob.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                if (blob.ReadByte() != -1)
                {
                    throw new InvalidDataException("Physical blob grew during the read.");
                }
            }

            ct.ThrowIfCancellationRequested();
            return new(descriptor, blob, bytes, mapping, verification);
        }
        catch
        {
            mapping?.Dispose();
            await blob.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DocumentDescriptor?> FindAsync(DocumentId id, CancellationToken ct = default)
    {
        RequireId(id);
        using FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false);
        using Catalog catalog = OpenCatalog();
        return catalog.Find(id);
    }

    public Task SetReferenceAsync(string name, DocumentId id, CancellationToken ct = default) => RetainAsync(1, name, id, ct);
    public Task PinAsync(string name, DocumentId id, CancellationToken ct = default) => RetainAsync(2, name, id, ct);
    public Task<bool> RemoveReferenceAsync(string name, CancellationToken ct = default) => ReleaseAsync(1, name, ct);
    public Task<bool> UnpinAsync(string name, CancellationToken ct = default) => ReleaseAsync(2, name, ct);

    public async Task<DocumentDescriptor?> ResolveReferenceAsync(string name, CancellationToken ct = default)
    {
        RequireName(name);
        using FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false);
        using Catalog catalog = OpenCatalog();
        return catalog.Resolve(1, name);
    }

    public async Task<bool> DeleteAsync(DocumentId id, CancellationToken ct = default)
    {
        RequireId(id);
        using FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false);
        using Catalog catalog = OpenCatalog();
        using SqliteTransaction tx = catalog.Begin();
        int removed = catalog.Delete(id, tx);
        fault?.Invoke(StoreFault.BeforeReferenceCommit);
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return removed > 0;
    }

    public async Task<CollectionResult> CollectAsync(CancellationToken ct = default)
    {
        using FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false);
        return Directory.EnumerateFileSystemEntries(files.Actions).Any()
            || Directory.EnumerateFileSystemEntries(files.Operations).Any()
            ? throw new InvalidDataException("This CAS contains artifact roots. Collect through the artifact CAS command.")
            : (await CollectLeasedAsync(new HashSet<string>(StringComparer.Ordinal), apply: true, ct)
                .ConfigureAwait(false)).Result;
    }

    // The artifact collector already holds the same namespace lease while it reads operation pins and actions.
    internal static Task<(CollectionResult Result, long Bytes, long Protected)> CollectArtifactNamespaceAsync(
        string root, IReadOnlySet<string> artifactRoots, bool apply, CancellationToken ct) =>
        new DocumentStore(root, Catalog.CollectProduction).CollectLeasedAsync(artifactRoots, apply, ct);

    private async Task<(CollectionResult Result, long Bytes, long Protected)> CollectLeasedAsync(
        IReadOnlySet<string> artifactRoots, bool apply, CancellationToken ct)
    {
        using Catalog catalog = OpenCatalog();
        int documents = 0, blobs = 0, active = 0, staged = 0;
        long bytes = 0, protectedBlobs = artifactRoots.Count;
        foreach (string path in Directory.EnumerateFiles(files.Blobs))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (!ArtifactStoreFiles.IsCanonicalHex(name, 64) || artifactRoots.Contains(name))
            {
                continue;
            }

            if (catalog.IsRetained(name))
            {
                protectedBlobs++;
                continue;
            }

            CheckPath(path);
            if (!IsUnleased(path))
            {
                active++;
                protectedBlobs++;
                continue;
            }

            bytes += new FileInfo(path).Length;
            if (apply)
            {
                using SqliteTransaction tx = catalog.Begin();
                documents += catalog.Prune(name, tx);
                await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                fault?.Invoke(StoreFault.AfterPrune);
                File.Delete(path);
            }

            blobs++;
        }

        if (apply)
        {
            foreach (string path in Directory.EnumerateFiles(files.Staging, "document-*.tmp"))
            {
                ct.ThrowIfCancellationRequested();
                CheckPath(path);
                File.Delete(path);
                staged++;
            }
        }

        return (new(documents, blobs, active, staged), bytes, protectedBlobs);
    }

    public async Task<MigrationResult> RehearseMigrationAsync(string jsonPath, LegacyDigest expected,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        using FileStream input = new(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        long length = input.Length;
        if (length is < 1 or > WorkflowDocuments.MaxStoredBytes)
        {
            throw new InvalidDataException("External JSON manifest exceeds the supported length.");
        }

        byte[] snapshot = new byte[(int)length];
        await input.ReadExactlyAsync(snapshot, ct).ConfigureAwait(false);
        return input.ReadByte() != -1
            ? throw new InvalidDataException("External JSON grew during the read.")
            : await MigrateAsync(snapshot, expected, null, null, ct).ConfigureAwait(false);
    }

    /// <summary>Verifies and durably retains both representations under one named archival prefix.</summary>
    public Task<MigrationResult> ArchiveWorkflowAsync(ReadOnlyMemory<byte> snapshot, LegacyDigest expected, string archive,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);
        string legacyReference = archive + "/legacy";
        string binaryReference = archive + "/binary";
        RequireName(legacyReference);
        RequireName(binaryReference);
        ct.ThrowIfCancellationRequested();
        WorkflowDocuments.CheckSize(snapshot.Length);
        return production
            ? MigrateAsync(snapshot.ToArray(), expected, legacyReference, binaryReference, ct)
            : throw new InvalidOperationException("Workflow archives require a production document catalog.");
    }

    private async Task<MigrationResult> MigrateAsync(byte[] snapshot, LegacyDigest expected,
        string? legacyReference, string? binaryReference, CancellationToken ct)
    {
        (EncodedDocument legacy, WorkflowDocument source) = WorkflowDocuments.ImportOwnedJson(snapshot, expected);
        EncodedDocument binary = WorkflowDocuments.Encode(source, production);
        WorkflowDocuments.NormalizeProjection(source, preserveDependencyOrder: production);
        byte[] projected = WorkflowDocuments.Project(source);
        if (!projected.AsSpan().SequenceEqual(WorkflowDocuments.Project(
            WorkflowDocuments.ReadBinary(binary.Bytes, binary.Descriptor.Codec))))
        {
            throw new InvalidDataException("Binary rehearsal would change supported normalized workflow values.");
        }

        MigrationResult result = new(legacy.Descriptor, binary.Descriptor, DocumentDescriptor.Hash(projected));
        using FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false);
        using Catalog catalog = OpenCatalog();
        await PublishBlobAsync(legacy, ct).ConfigureAwait(false);
        await PublishBlobAsync(binary, ct).ConfigureAwait(false);
        using SqliteTransaction tx = catalog.Begin();
        catalog.Insert(legacy.Descriptor, tx);
        catalog.Insert(binary.Descriptor, tx);
        catalog.Record(result, tx);
        if (legacyReference is not null && binaryReference is not null)
        {
            catalog.Archive(legacyReference, legacy.Descriptor.Id, tx);
            catalog.Archive(binaryReference, binary.Descriptor.Id, tx);
        }

        await CommitAsync(tx, ct).ConfigureAwait(false);
        fault?.Invoke(StoreFault.BeforeReturn);
        return result;
    }

    public async Task<string?> FindEquivalenceAsync(DocumentId legacy, DocumentId binary, CancellationToken ct = default)
    {
        RequireId(legacy);
        RequireId(binary);
        using FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false);
        using Catalog catalog = OpenCatalog();
        return catalog.Equivalence(legacy, binary);
    }

    private async Task PublishBlobAsync(EncodedDocument document, CancellationToken ct)
    {
        string path = BlobPath(document.Descriptor.Blob);
        CheckPath(path);
        if (!File.Exists(path))
        {
            fault?.Invoke(StoreFault.BeforeStage);
            string stage = Path.Combine(files.Staging, $"document-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (FileStream output = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous))
                {
                    int half = document.Bytes.Length / 2;
                    await output.WriteAsync(document.Bytes.AsMemory(0, half), ct).ConfigureAwait(false);
                    fault?.Invoke(StoreFault.DuringStage);
                    await output.WriteAsync(document.Bytes.AsMemory(half), ct).ConfigureAwait(false);
                    fault?.Invoke(StoreFault.AfterWrite);
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    RandomAccess.FlushToDisk(output.SafeFileHandle);
                    fault?.Invoke(StoreFault.AfterFlush);
                }

                fault?.Invoke(StoreFault.BeforePublish);
                ct.ThrowIfCancellationRequested();
                DurableByteWriter.PublishNewFile(stage, path);
                fault?.Invoke(StoreFault.AfterPublish);
            }
            finally
            {
                File.Delete(stage);
            }
        }

        await ArtifactStoreFiles.VerifyAsync(path, document.Descriptor.Blob.Value, document.Bytes.Length, ct)
            .ConfigureAwait(false);
    }

    private async Task CommitAsync(SqliteTransaction tx, CancellationToken ct)
    {
        fault?.Invoke(StoreFault.BeforeCommit);
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        fault?.Invoke(StoreFault.AfterCommit);
    }

    private async Task RetainAsync(int role, string name, DocumentId id, CancellationToken ct)
    {
        RequireId(id);
        RequireName(name);
        using FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false);
        using Catalog catalog = OpenCatalog();
        _ = catalog.Find(id) ?? throw new KeyNotFoundException("Workflow document is absent.");
        catalog.Retain(role, name, id);
    }

    private async Task<bool> ReleaseAsync(int role, string name, CancellationToken ct)
    {
        RequireName(name);
        using FileStream lease = await AcquireAsync(create: false, ct).ConfigureAwait(false);
        using Catalog catalog = OpenCatalog();
        using SqliteTransaction tx = catalog.Begin();
        int removed = catalog.Release(role, name, tx);
        fault?.Invoke(StoreFault.BeforeReferenceCommit);
        ct.ThrowIfCancellationRequested();
        await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return removed > 0;
    }

    private string BlobPath(BlobId id) => files.Blob(id.Value);

    private Catalog OpenCatalog(bool initialize = false)
    {
        CheckPath(database);
        CheckPath(database + "-wal");
        CheckPath(database + "-shm");
        CheckPath(database + "-journal");
        return new(database, catalogVersion, initialize);
    }

    private async Task<FileStream> AcquireAsync(bool create, CancellationToken ct)
    {
        CheckAncestors(Root);
        CheckPath(files.Root);
        CheckPath(Path.Combine(files.Root, ".lock"));
        CheckPath(Path.Combine(files.Root, ".cas-format"));
        CheckPath(Path.Combine(files.Root, ".cas-format.tmp"));
        CheckPath(files.Blobs);
        CheckPath(files.Staging);
        CheckPath(files.Actions);
        CheckPath(files.Operations);
        FileStream lease = await files.OpenLeaseAsync(create, ct, requireDocumentFormat: production,
            requireIndexedActions: requireIndexedActions).ConfigureAwait(false);
        return lease;
    }

    private static bool IsUnleased(string path)
    {
        try
        {
            using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None, 1);
            return true;
        }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33 ||
            (OperatingSystem.IsMacOS() && (exception.HResult & 0xffff) == 35))
        {
            return false;
        }
    }

    private static void RequireId(DocumentId id)
    {
        if (!ArtifactStoreFiles.IsCanonicalHex(id.Value, 64))
        {
            throw new ArgumentException("Expected a valid document ID.", nameof(id));
        }
    }

    private static void RequireName(string name)
    {
        if (name is null || name.Length is < 1 or > 160 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Retention names require 1 to 160 characters without controls.", nameof(name));
        }
    }

    private static void CheckAncestors(string path)
    {
        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
        {
            CheckPath(parent);
        }
    }

    private static void CheckPath(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Document store paths cannot traverse symlinks or reparse points.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
