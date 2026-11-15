#pragma warning disable IDE0046 // Keep explicit verification/guard branches in source integrity checks.
using System.Security.Cryptography;
using System.Text.Json;
using LoreVcs;
using LoreVcs.Types;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace CMakeWorkflow.Sources;

// The manifest, including the Lore addresses, is owned by CMakeWorkflow.
// Lore is used only for blob storage. Its disk backend requires a private,
// local .lore metadata directory; it never operates on the user's repository.
// Physical addressing is deliberately outside the content-addressed manifest.
// A different storage backend must not change SnapshotId.
internal sealed record LorePointer(string Path, string LoreHash, string LoreContext);
internal sealed record LoreBindings(int SchemaVersion, string SnapshotId, LorePointer[] Files);

public sealed class LoreSnapshotStore(string storeRoot) : ISourceSnapshotStore
{
    private const int SchemaVersion = 1;
    private const int MaxFileBytes = 128 * 1024 * 1024; // LoreVcs 0.10.0 Put takes byte[].
    private static readonly byte[] Partition = "CMWF-SNAPSHOT-V1"u8.ToArray(); // 16 bytes
    private static readonly byte[] Context = "CMWF-SOURCE-0001"u8.ToArray(); // 16 bytes
    private readonly string _storeRoot = Path.GetFullPath(storeRoot ?? throw new ArgumentNullException(nameof(storeRoot)));

    public string Capture(string sourceRoot, IReadOnlyCollection<string> relativeFiles)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);
        ArgumentNullException.ThrowIfNull(relativeFiles);
        if (relativeFiles.Count == 0)
        {
            throw new ArgumentException("An explicit, non-empty file selection is required.", nameof(relativeFiles));
        }

        string source = Path.GetFullPath(sourceRoot);
        // Capturing a repository into its own source tree can recursively ingest the
        // snapshot store when untracked files are opted in. Require out-of-tree storage.
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string rootPrefix = Path.EndsInDirectorySeparator(source)
            ? source : source + Path.DirectorySeparatorChar;
        if (_storeRoot.Equals(Path.TrimEndingDirectorySeparator(source), pathComparison) ||
            _storeRoot.StartsWith(rootPrefix, pathComparison))
        {
            throw new InvalidDataException("Snapshot store must reside outside the captured source tree.");
        }
        SortedSet<string> selected = new(StringComparer.Ordinal);
        HashSet<string> portableNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in relativeFiles)
        {
            SourcePathPolicy.Validate(relative);
            if (!selected.Add(relative) || !portableNames.Add(relative))
            {
                throw new InvalidDataException($"Duplicate or case-colliding path: {relative}");
            }
        }

        _ = Directory.CreateDirectory(_storeRoot);
        SnapshotFile[] entries;
        LorePointer[] pointers;
        // Closing the Lore handle before manifest publication is intentional:
        // the manifest must never become visible before its blobs have been flushed.
        using (StorageSession lore = new(Path.Combine(_storeRoot, "lore"), initialize: true))
        {
            List<SnapshotFile> list = new(selected.Count);
            List<LorePointer> locations = new(selected.Count);
            foreach (string relative in selected)
            {
                string full = ResolveSourceFile(source, relative);
                FileInfo before = new(full);
                if (before.Length > MaxFileBytes)
                {
                    throw new InvalidDataException($"File exceeds current Lore adapter limit ({MaxFileBytes} bytes): {relative}");
                }
                byte[] bytes = File.ReadAllBytes(full);
                FileInfo after = new(full);
                if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc || bytes.Length != after.Length)
                {
                    throw new IOException($"File changed during capture: {relative}");
                }

                string sha256 = Sha256(bytes);
                LoreAddress address = lore.Put(bytes);
                list.Add(new SnapshotFile(relative, sha256, bytes.LongLength));
                locations.Add(new LorePointer(relative,
                    Convert.ToHexString(address.Hash.Data),
                    Convert.ToHexString(address.Context.Data)));
            }
            entries = [.. list];
            pointers = [.. locations];
        }

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new SnapshotManifest(SchemaVersion, entries), SourceSnapshotJson.Default.SnapshotManifest);
        string snapshotId = Sha256(manifestBytes);
        byte[] bindingBytes = JsonSerializer.SerializeToUtf8Bytes(
            new LoreBindings(SchemaVersion, snapshotId, pointers), SourceSnapshotJson.Default.LoreBindings);
        string manifests = Path.Combine(_storeRoot, "manifests");
        _ = Directory.CreateDirectory(manifests);
        // Publish physical locators first, then the immutable logical manifest.
        // A crash between writes leaves an unused binding, never a visible incomplete snapshot.
        PublishImmutable(Path.Combine(manifests, snapshotId + ".lore.json"), bindingBytes);
        PublishImmutable(Path.Combine(manifests, snapshotId + ".json"), manifestBytes);
        return snapshotId;
    }

    public SnapshotManifest Inspect(string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ValidateHash(snapshotId, 64);
        string path = Path.Combine(_storeRoot, "manifests", snapshotId + ".json");
        byte[] data = File.ReadAllBytes(path);
        if (!Sha256(data).Equals(snapshotId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest hash does not match snapshot ID.");
        }
        SnapshotManifest manifest = JsonSerializer.Deserialize(data, SourceSnapshotJson.Default.SnapshotManifest)
            ?? throw new InvalidDataException("Invalid snapshot manifest.");
        if (manifest.SchemaVersion != SchemaVersion || manifest.Files is null)
        {
            throw new InvalidDataException("Unsupported snapshot manifest schema.");
        }
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        string? previous = null;
        foreach (SnapshotFile file in manifest.Files)
        {
            SourcePathPolicy.Validate(file.Path);
            if (!unique.Add(file.Path) || (previous is not null && string.CompareOrdinal(previous, file.Path) >= 0))
            {
                throw new InvalidDataException("Snapshot paths are duplicated or not canonically sorted.");
            }
            ValidateHash(file.Sha256, 64);
            if (file.Size is < 0 or > MaxFileBytes)
            {
                throw new InvalidDataException("Invalid file length in snapshot.");
            }
            previous = file.Path;
        }
        if (!JsonSerializer.SerializeToUtf8Bytes(manifest, SourceSnapshotJson.Default.SnapshotManifest)
                .AsSpan().SequenceEqual(data))
        {
            throw new InvalidDataException("Noncanonical snapshot manifest encoding.");
        }
        return manifest;
    }

    public void Materialize(string snapshotId, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(destination);
        SnapshotManifest manifest = Inspect(snapshotId);
        LoreBindings bindings = ReadBindings(snapshotId, manifest);
        string final = Path.GetFullPath(destination);
        if (Directory.Exists(final) || File.Exists(final))
        {
            throw new IOException("The destination must not already exist.");
        }
        string parent = Path.GetDirectoryName(final) ?? throw new IOException("Invalid destination.");
        _ = Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".cmwf-source-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(stage);
        try
        {
            using (StorageSession lore = new(Path.Combine(_storeRoot, "lore"), initialize: false))
            {
                for (int i = 0; i < manifest.Files.Count; i++)
                {
                    SnapshotFile entry = manifest.Files[i];
                    LorePointer pointer = bindings.Files[i];
                    byte[] bytes = lore.Get(new LoreAddress
                    {
                        Hash = new LoreHash(Convert.FromHexString(pointer.LoreHash)),
                        Context = new LoreContext(Convert.FromHexString(pointer.LoreContext))
                    });
                    if (bytes.LongLength != entry.Size || !Sha256(bytes).Equals(entry.Sha256, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"Corrupted Lore content for {entry.Path}");
                    }
                    string path = Path.Combine(stage, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, bytes);
                }
            }
            Directory.Move(stage, final); // same-filesystem publication of complete tree
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, recursive: true);
            }
        }
    }

    private LoreBindings ReadBindings(string snapshotId, SnapshotManifest manifest)
    {
        byte[] raw = File.ReadAllBytes(Path.Combine(_storeRoot, "manifests", snapshotId + ".lore.json"));
        LoreBindings binding = JsonSerializer.Deserialize(raw, SourceSnapshotJson.Default.LoreBindings)
            ?? throw new InvalidDataException("Invalid Lore bindings.");
        if (binding.SchemaVersion != SchemaVersion || binding.SnapshotId != snapshotId ||
            binding.Files is null || binding.Files.Length != manifest.Files.Count)
        {
            throw new InvalidDataException("Lore bindings do not match the manifest.");
        }
        for (int i = 0; i < binding.Files.Length; i++)
        {
            LorePointer entry = binding.Files[i];
            if (entry.Path != manifest.Files[i].Path)
            {
                throw new InvalidDataException("Lore binding path mismatch.");
            }
            ValidateHash(entry.LoreHash, 64);
            ValidateHash(entry.LoreContext, 32);
        }
        if (!JsonSerializer.SerializeToUtf8Bytes(binding, SourceSnapshotJson.Default.LoreBindings)
                .AsSpan().SequenceEqual(raw))
        {
            throw new InvalidDataException("Noncanonical Lore bindings.");
        }
        return binding;
    }

    private static void PublishImmutable(string target, byte[] bytes)
    {
        if (File.Exists(target))
        {
            if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidDataException("Immutable snapshot metadata changed.");
            }
            return;
        }
        string temp = Path.Combine(Path.GetDirectoryName(target)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            try
            {
                File.Move(temp, target);
            }
            catch (IOException) when (File.Exists(target))
            {
                if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidDataException("Conflicting snapshot metadata.");
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static string ResolveSourceFile(string root, string relative)
    {
        // This adapter captures only regular files, never links or reparse points.
        string current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Source root must not be a symlink.");
        }
        string[] parts = relative.Split('/');
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Symlink/reparse point in source path: {relative}");
            }
        }
        if ((File.GetAttributes(current) & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException($"Not a regular file: {relative}");
        }
        return current;
    }

    private static void ValidateHash(string hash, int hexChars)
    {
        if (hash is null || hash.Length != hexChars || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Invalid hash in snapshot manifest.");
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class StorageSession : IDisposable
    {
        private readonly LoreGlobalArgs _global;
        private readonly ulong _handle;
        private bool _closed;

        public StorageSession(string root, bool initialize)
        {
            _global = new LoreGlobalArgs { Offline = true, RepositoryPath = root };
            string dotLore = Path.Combine(root, ".lore");
            if (initialize && !Directory.Exists(dotLore))
            {
                // Disk-backed StorageOpen requires an initialized Lore directory.
                // Use a private offline repository as the storage container, never
                // initialize or modify the developer's Git/Lore working tree.
                string manifests = Path.Combine(Path.GetDirectoryName(root)!, "manifests");
                if ((Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) ||
                    (Directory.Exists(manifests) && Directory.EnumerateFileSystemEntries(manifests).Any()))
                {
                    throw new InvalidDataException(
                        "Lore metadata is missing but storage or snapshot metadata already exists; refusing to replace it.");
                }
                _ = Directory.CreateDirectory(root);
                using LoreRepositoryCreateArgs create = new() { RepositoryUrl = "cmwf-snapshots" };
                _ = Lore.RepositoryCreate(_global, create).Wait();
            }
            if (!Directory.Exists(dotLore))
            {
                throw new InvalidDataException("Lore snapshot storage is not initialized (missing .lore).");
            }
            using LoreStorageOpenArgs args = new() { RepositoryPath = root, InMemory = false };
            ulong handle = 0;
            _ = Lore.StorageOpen(_global, args)
                .Callback((evt, _) =>
                {
                    if (evt.Tag == LoreEventTag.STORAGE_OPENED)
                    {
                        handle = evt.GetData<LoreStorageOpenedEventDataFFI>().HandleId;
                    }
                })
                .Wait();
            if (handle == 0)
            {
                throw new IOException("Lore did not return a storage handle.");
            }
            _handle = handle;
        }

        public LoreAddress Put(byte[] bytes)
        {
            using LoreStoragePutArgs args = new()
            {
                Handle = new LoreStore(_handle),
                Items = [new LoreStoragePutItem
                {
                    Id = 1UL,
                    Partition = new LorePartition(Partition),
                    Context = new LoreContext(Context),
                    Data = bytes,
                    RemoteWrite = false
                }]
            };
            LoreStoragePutItemCompleteEventData[] events = [.. Lore.StoragePut(_global, args)
                .FilterByType([LoreEventTag.STORAGE_PUT_ITEM_COMPLETE]).Collect()
                .OfType<LoreStoragePutItemCompleteEventData>()];
            if (events.Length != 1 || events[0].ErrorCode != (int)LoreErrorCode.NONE)
            {
                throw new IOException("Lore StoragePut failed.");
            }
            return events[0].Address;
        }

        public byte[] Get(LoreAddress address)
        {
            using LoreStorageGetArgs args = new()
            {
                Handle = new LoreStore(_handle),
                Items = [new LoreStorageGetItem
                {
                    Id = 1UL,
                    Partition = new LorePartition(Partition),
                    Address = address,
                    Streaming = false
                }]
            };
            List<LoreEvent> events = [.. Lore.StorageGet(_global, args)
                .FilterByType([LoreEventTag.STORAGE_GET_DATA, LoreEventTag.STORAGE_GET_ITEM_COMPLETE])
                .Collect()];
            LoreStorageGetItemCompleteEventData[] completed = [.. events.OfType<LoreStorageGetItemCompleteEventData>()];
            if (completed.Length != 1 || completed[0].ErrorCode != (int)LoreErrorCode.NONE)
            {
                throw new IOException("Lore StorageGet failed or content was evicted.");
            }
            using MemoryStream buffer = new();
            foreach (LoreStorageGetDataEventData chunk in events.OfType<LoreStorageGetDataEventData>())
            {
                if (chunk.Offset != (ulong)buffer.Length)
                {
                    throw new InvalidDataException("Out-of-order Lore fragment data.");
                }
                buffer.Write(chunk.Bytes);
            }
            return buffer.ToArray();
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            using LoreStorageCloseArgs args = new() { Handle = new LoreStore(_handle) };
            _ = Lore.StorageClose(_global, args).Wait();
            _closed = true;
        }
    }
}
