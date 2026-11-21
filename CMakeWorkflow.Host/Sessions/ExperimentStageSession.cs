using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Sources;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    /// <summary>Verifies and publishes a source snapshot and manifest without admitting execution.</summary>
    public Task<ExperimentStageReceipt> StageExperimentAsync(ExperimentStageRequest request,
        CancellationToken ct) => ApplicationCallAsync(request, async (paths, token) =>
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("Experiment staging requires a Linux Host.");
        }

        if (!IsStageHash(request.ExperimentId) || !IsStageHash(request.SourceSnapshotId) ||
            !IsStageHash(request.ManifestSha256) ||
            request.Manifest is not { Length: > 0 and <= ExperimentStageRequest.MaximumManifestCharacters } ||
            request.Files is not { Count: > 0 and <= ExperimentStageRequest.MaximumFiles })
        {
            throw new ArgumentException("Invalid experiment staging identity or bundle size.");
        }

        byte[] manifestBytes = Encoding.UTF8.GetBytes(request.Manifest);
        if (Convert.ToHexString(SHA256.HashData(manifestBytes)) != request.ManifestSha256)
        {
            throw new InvalidDataException("Experiment manifest SHA-256 mismatch.");
        }

        Dictionary<string, byte[]> files = DecodeFiles(request.Files);
        SnapshotFile[] evidence = [.. files.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new SnapshotFile(pair.Key, Convert.ToHexString(SHA256.HashData(pair.Value)),
                pair.Value.LongLength))];
        if (LoreSnapshotStore.GetSnapshotId(evidence) != request.SourceSnapshotId)
        {
            throw new InvalidDataException("Experiment source snapshot SHA-256 mismatch.");
        }

        string root = RequireStagePath(paths, Path.Combine(paths.WorkspaceDirectory, ".cmakeworkflow", "experiments"));
        _ = Directory.CreateDirectory(root);
        string stage = RequireStagePath(paths, Path.Combine(root, request.ExperimentId));
        string manifest = RequireStagePath(paths, Path.Combine(stage, "workflow.json"));
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
                foreach ((string relative, byte[] bytes) in files)
                {
                    string file = Path.Combine(scratch.FullName, relative);
                    _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await using FileStream output = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await output.WriteAsync(bytes, token);
                }

                string captured = await store.CaptureAsync(scratch.FullName, files.Keys, token);
                if (captured != request.SourceSnapshotId)
                {
                    throw new InvalidDataException("Captured source does not match the approved snapshot.");
                }

                _ = Directory.CreateDirectory(staging);
                await store.MaterializeAsync(captured, Path.Combine(staging, "source"), token);
                await File.WriteAllTextAsync(Path.Combine(staging, "source.sha256"), captured, token);
                await File.WriteAllBytesAsync(Path.Combine(staging, "workflow.json"), manifestBytes, token);
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
        return new ExperimentStageReceipt(manifest, request.ManifestSha256, request.SourceSnapshotId);
    }, ct);

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
