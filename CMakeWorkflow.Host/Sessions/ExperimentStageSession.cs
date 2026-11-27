using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Sources;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    /// <summary>Writes a bounded chunk to the workspace staging area and returns the saved offset.</summary>
    public Task<long> WriteExperimentSourceChunkAsync(ExperimentSourceChunk request, CancellationToken ct) =>
        ApplicationCallAsync(request, async (paths, token) =>
        {
            if (!OperatingSystem.IsLinux() || !IsStageHash(request.ExperimentId) || !IsStageHash(request.Sha256) ||
                request.Size is <= 0 or > ExperimentSourceBundle.MaximumArchiveBytes || request.Offset < 0 ||
                request.Data.Length > ExperimentSourceBundle.ChunkBytes ||
                request.Offset > request.Size - request.Data.Length)
            {
                throw new ArgumentException("Invalid experiment source chunk.");
            }

            string path = SourceUploadPath(paths, request.ExperimentId, request.Sha256);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using FileStream lease = await LockStageAsync(paths, request.ExperimentId, token);
            await using FileStream output = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (output.Length > request.Size || request.Offset > output.Length)
            {
                throw new InvalidDataException("Invalid experiment source upload offset or size.");
            }

            if (request.Data.Length == 0 || request.Offset < output.Length)
            {
                return output.Length;
            }
            output.Position = request.Offset;
            await output.WriteAsync(request.Data, token);
            await output.FlushAsync(token);
            return output.Length;
        }, ct);

    /// <summary>Verifies and publishes a source snapshot and manifest without admitting execution.</summary>
    public Task<ExperimentStageReceipt> StageExperimentAsync(ExperimentStageRequest request,
        CancellationToken ct) => ApplicationCallAsync(request, async (paths, token) =>
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("Experiment staging requires a Linux Host.");
        }

        bool binary = request.BinaryManifest.HasValue;
        if (!IsStageHash(request.ExperimentId) || !IsStageHash(request.SourceSnapshotId) ||
            !IsStageHash(request.ManifestSha256) ||
            (binary ? request.Manifest is not { Length: 0 } || request.Bundle is null ||
                       request.BinaryManifest is not { Length: >= 16 and <= WorkflowManifestPreparation.MaximumBytes }
                    : request.Manifest is not { Length: > 0 and <= ExperimentStageRequest.MaximumManifestCharacters }) ||
            (request.Bundle is null
                ? request.Files is not { Count: > 0 and <= ExperimentStageRequest.MaximumFiles }
                : request.Files is not { Count: 0 } || !IsStageHash(request.Bundle.Sha256) ||
                  request.Bundle.Size is <= 0 or > ExperimentSourceBundle.MaximumArchiveBytes ||
                  request.Bundle.FileCount is <= 0 or > ExperimentSourceBundle.MaximumFiles))
        {
            throw new ArgumentException("Invalid experiment staging identity or bundle size.");
        }

        byte[] manifestBytes = request.BinaryManifest?.ToArray() ?? Encoding.UTF8.GetBytes(request.Manifest);
        if (Convert.ToHexString(SHA256.HashData(manifestBytes)) != request.ManifestSha256)
        {
            throw new InvalidDataException("Experiment manifest SHA-256 mismatch.");
        }
        WorkflowDocument? workflow = null;
        if (binary)
        {
            using DurableAcceptanceStore ledger = new(paths.WorkspaceDirectory, readOnly: true);
            if (!ledger.UsesDocumentPlans)
            {
                throw new InvalidOperationException("native_experiment_requires_fresh_execution_workspace");
            }
            workflow = VerifyFrame(manifestBytes, request.ManifestSha256);
            _ = WorkflowPlanner.ValidateGraph(workflow, []);
            foreach (WorkflowContext context in workflow.Contexts.Values)
            {
                if (context.Toolchain is { EnvironmentProfileId: "linux-managed-clang" } tooling)
                {
                    string identity = tooling.EnvironmentLockSha256
                        ?? throw new InvalidDataException("The native experiment has no approved managed environment lock.");
                    ToolchainEnvironmentLock locked = await ManagedEnvironmentLocks.ReadBinaryAsync(identity, token);
                    if (locked.Generator != tooling.Generator || locked.Host.Architecture != tooling.HostArchitecture ||
                        locked.Target.Architecture != tooling.TargetArchitecture)
                    {
                        throw new InvalidDataException("The approved managed environment differs from the native experiment.");
                    }
                }
            }
        }

        Dictionary<string, byte[]>? inline = request.Bundle is null ? DecodeFiles(request.Files) : null;
        if (inline is not null && LoreSnapshotStore.GetSnapshotId([.. inline.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                new SnapshotFile(pair.Key, Convert.ToHexString(SHA256.HashData(pair.Value)), pair.Value.LongLength))]) !=
            request.SourceSnapshotId)
        {
            throw new InvalidDataException("Experiment source snapshot SHA-256 mismatch.");
        }

        await using FileStream lease = await LockStageAsync(paths, request.ExperimentId, token);
        string root = RequireStagePath(paths, Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "experiments"));
        _ = Directory.CreateDirectory(root);
        string stage = RequireStagePath(paths, Path.Combine(root, request.ExperimentId));
        string manifestName = binary ? "workflow.capnp" : "workflow.json";
        string manifest = RequireStagePath(paths, Path.Combine(stage, manifestName));
        string source = RequireStagePath(paths, Path.Combine(stage, "source"));
        string binding = RequireStagePath(paths, Path.Combine(stage, "source.sha256"));

        LoreSnapshotStore store = new(SourceStorePaths.ForWorkspace(paths.WorkspaceDirectory));
        if (!Directory.Exists(stage))
        {
            DirectoryInfo scratch = Directory.CreateTempSubdirectory("cmwf-stage-");
            // Use a sibling for atomic publication within the workspace filesystem.
            string staging = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"));
            try
            {
                IReadOnlyCollection<string> files = request.Bundle is { } bundle
                    ? await ExtractBundleAsync(paths, request, bundle, scratch.FullName, token)
                    : await ExtractInlineAsync(inline!, scratch.FullName, token);
                string captured = await store.CaptureAsync(scratch.FullName, files, token);
                if (captured != request.SourceSnapshotId)
                {
                    throw new InvalidDataException("Captured source does not match the approved snapshot.");
                }

                _ = Directory.CreateDirectory(staging);
                await store.MaterializeAsync(captured, Path.Combine(staging, "source"), token);
                await File.WriteAllTextAsync(Path.Combine(staging, "source.sha256"), captured, token);
                await File.WriteAllBytesAsync(Path.Combine(staging, manifestName), manifestBytes, token);
                token.ThrowIfCancellationRequested();
                try { Directory.Move(staging, stage); }
                catch (IOException) when (Directory.Exists(stage)) { }
            }
            finally
            {
                scratch.Delete(recursive: true);
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }

        paths.Read(manifest);
        paths.Read(binding);
        if (await File.ReadAllTextAsync(binding, token) != request.SourceSnapshotId ||
            !(await File.ReadAllBytesAsync(manifest, token)).AsSpan().SequenceEqual(manifestBytes))
        {
            throw new InvalidDataException("Experiment identity conflicts with the published stage.");
        }

        SnapshotManifest snapshot = await store.VerifyAsync(request.SourceSnapshotId, token);
        await WorkflowSourceSnapshotExecution.VerifyExistingMaterializationAsync(snapshot, source, token);
        if (workflow is not null)
        {
            _ = WorkflowSourceSnapshotExecution.ValidateSourceLayout(paths, manifest, workflow);
        }
        return new ExperimentStageReceipt(manifest, request.ManifestSha256, request.SourceSnapshotId);
    }, ct);

    private static Task<FileStream> LockStageAsync(WorkspaceReadPaths paths, string experimentId, CancellationToken ct)
    {
        string path = RequireStagePath(paths, Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow",
            "experiment-locks", experimentId + ".lock"));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return ManagedToolStore.LockAsync(path, ct);
    }

    private static string SourceUploadPath(WorkspaceReadPaths paths, string experimentId, string sha256) =>
        RequireStagePath(paths, Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "experiment-uploads",
            experimentId, sha256 + ".zip"));

    private static async Task<IReadOnlyCollection<string>> ExtractInlineAsync(Dictionary<string, byte[]> files,
        string destination, CancellationToken ct)
    {
        foreach ((string relative, byte[] bytes) in files)
        {
            await using FileStream output = CreateSourceFile(destination, relative);
            await output.WriteAsync(bytes, ct);
        }
        return files.Keys;
    }

    private static async Task<IReadOnlyCollection<string>> ExtractBundleAsync(WorkspaceReadPaths paths,
        ExperimentStageRequest request, ExperimentSourceBundle bundle, string destination, CancellationToken ct)
    {
        string path = SourceUploadPath(paths, request.ExperimentId, bundle.Sha256);
        await using FileStream input = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (input.Length != bundle.Size)
        {
            throw new InvalidDataException("Experiment source upload is incomplete.");
        }
        if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != bundle.Sha256)
        {
            input.SetLength(0);
            throw new InvalidDataException("Experiment source upload SHA-256 mismatch. Retry the approved upload.");
        }
        await VerifyZipDirectoryAsync(input, bundle.FileCount, ct);
        input.Position = 0;
        await using ZipArchive zip = await ZipArchive.CreateAsync(input, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, cancellationToken: ct);
        if (zip.Entries.Count != bundle.FileCount)
        {
            throw new InvalidDataException("Experiment ZIP entry count does not match the plan.");
        }

        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string relative = entry.FullName;
            SourcePathPolicy.Validate(relative);
            if (relative.Length > 1024 || !files.Add(relative) ||
                entry.ExternalAttributes is not (ExperimentSourceBundle.RegularFile or ExperimentSourceBundle.ExecutableFile) ||
                entry.Length is < 0 or > ExperimentSourceBundle.MaximumFileBytes ||
                entry.Length > ExperimentSourceBundle.MaximumSourceBytes - total)
            {
                throw new InvalidDataException($"Invalid, duplicate or oversized ZIP entry: {relative}");
            }
            total += entry.Length;
            await using FileStream output = CreateSourceFile(destination, relative);
            await using Stream contents = await entry.OpenAsync(ct);
            await CopyEntryAsync(contents, output, entry.Length, ct);
            if (entry.ExternalAttributes == ExperimentSourceBundle.ExecutableFile && OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(output.Name, File.GetUnixFileMode(output.Name) | UnixFileMode.UserExecute);
            }
        }
        return files.AsEnumerable().Contains("CMakeLists.txt", StringComparer.Ordinal) ? files
            : throw new InvalidDataException("Experiment source requires CMakeLists.txt at its root.");
    }

    private static async Task VerifyZipDirectoryAsync(FileStream input, int fileCount, CancellationToken ct)
    {
        // The bundle wire format uses a single ZIP disk, with no archive comment or ZIP64 metadata.
        // Bound the actual count before ZipArchive allocates the entry objects.
        const int endRecordSize = 22;
        if (input.Length < endRecordSize)
        {
            throw new InvalidDataException("Missing ZIP directory.");
        }
        long endOffset = input.Length - endRecordSize;
        input.Position = endOffset;
        byte[] end = new byte[endRecordSize];
        await input.ReadExactlyAsync(end, ct);
        long start = BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(16));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != 0x06054B50 ||
            BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(4)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(8)) != fileCount ||
            BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(10)) != fileCount ||
            BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(20)) != 0 || start + size != endOffset)
        {
            throw new InvalidDataException("Invalid experiment ZIP directory.");
        }
        input.Position = start;
        byte[] entry = new byte[46];
        for (int i = 0; i < fileCount; i++)
        {
            if (input.Position > endOffset - entry.Length)
            {
                throw new InvalidDataException("Truncated experiment ZIP directory.");
            }
            await input.ReadExactlyAsync(entry, ct);
            int name = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(28));
            int extra = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(30));
            int comment = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(32));
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != 0x02014B50 || name is 0 or > 4096 ||
                extra > 4096 || comment != 0 || input.Position + name + extra > endOffset)
            {
                throw new InvalidDataException("Invalid experiment ZIP entry metadata.");
            }
            input.Position += name + extra;
        }
        if (input.Position != endOffset)
        {
            throw new InvalidDataException("Unexpected entries in the experiment ZIP directory.");
        }
    }

    private static async Task CopyEntryAsync(Stream input, Stream output, long size, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            copied += count;
            if (copied > size)
            {
                throw new InvalidDataException("ZIP entry exceeds its declared size.");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (copied != size)
        {
            throw new InvalidDataException("ZIP entry is shorter than its declared size.");
        }
    }

    private static FileStream CreateSourceFile(string destination, string relative)
    {
        string file = Path.GetFullPath(Path.Combine(destination, relative));
        if (!file.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Source file escapes the staging directory.");
        }
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    private static Dictionary<string, byte[]> DecodeFiles(IReadOnlyList<ExperimentSourceFile> files)
    {
        Dictionary<string, byte[]> result = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (ExperimentSourceFile file in files)
        {
            if (file?.Path is not { Length: > 0 and <= 1024 } || file.Base64 is null ||
                file.Base64.Length > (ExperimentStageRequest.MaximumSourceBytes - total + 2) / 3 * 4)
            {
                throw new InvalidDataException("Invalid experiment source path or bundle size.");
            }

            SourcePathPolicy.Validate(file.Path);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(file.Base64); }
            catch (FormatException error) { throw new InvalidDataException("Invalid source encoding.", error); }
            total += bytes.Length;
            if (total > ExperimentStageRequest.MaximumSourceBytes || !result.TryAdd(file.Path, bytes))
            {
                throw new InvalidDataException("Experiment source is too large or contains duplicate paths.");
            }
        }

        return result.Keys.Contains("CMakeLists.txt", StringComparer.Ordinal) ? result
            : throw new InvalidDataException("Experiment source requires CMakeLists.txt at its root.");
    }

    private static bool IsStageHash(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigitUpper);

    private static string RequireStagePath(WorkspaceReadPaths paths, string absolute)
    {
        string authorized = paths.Require(absolute);
        return authorized == absolute ? authorized
            : throw new ApplicationRefusal("path_not_allowed", "Experiment paths cannot be redirected.");
    }
}
