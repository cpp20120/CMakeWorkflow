using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Pipelines.Artifacts;
using CMakeWorkflow.Pipelines.GitHub;
using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Pipelines.Federation;

/// <summary>An artifact published to a caller-owned new local path, after provider-side validation.</summary>
/// <remarks>
/// For GitHub, Sha256 is the original ZIP digest, not a hash of the extracted directory.
/// Re-verification checks the receipt's individual file digests, not the discarded ZIP.
/// </remarks>
public sealed record FederatedArtifactFetchReceipt(
    FederatedArtifactKey Key, string Path, bool IsDirectory, string Sha256,
    IReadOnlyList<VerifiedArchiveFile> Files);

/// <summary>Read-only pipeline artifact federation on the durable ledger.</summary>
/// <remarks>
/// No dispatch, stage transition, provider retry, reconnect, or writable workspace path API.
/// Ledger.Get supplies the admitted manifest and exact stage/child binding for every request.
/// The Host source is chosen by the trusted caller from its already-connected workspace; its
/// host identity must be stable across reconnects to retain the same artifact key.
/// Caller-owned destinations must exclude hostile concurrent filesystem writers.
/// </remarks>
public sealed class FederatedArtifactCoordinator(
    PipelineLedger ledger, GitHubActionsArtifactCatalog? githubCatalog = null,
    GitHubActionsArtifactDownloader? githubDownloader = null)
{
    private readonly PipelineLedger ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly GitHubActionsArtifactCatalog? githubCatalog = githubCatalog;
    private readonly GitHubActionsArtifactDownloader? githubDownloader = githubDownloader;

    /// <summary>Observe exactly one terminal stage from the persisted pipeline operation.</summary>
    public async Task<IReadOnlyList<FederatedArtifact>> ListAsync(
        string pipelineOperationId, string stageId, IWorkflowHostArtifactSource? host = null,
        CancellationToken cancellationToken = default)
    {
        (PipelineRunSnapshot pipeline, PipelineStageSnapshot stage) = BoundStage(pipelineOperationId, stageId);
        if (stage.Kind == "github.actions")
        {
            return await RequireGitHubCatalog().ListAsync(pipeline, stageId, cancellationToken);
        }

        RequireHost(stage, host);
        OperationArtifactList catalog = await host!.ListAsync(cancellationToken);
        return MapHostCatalog(pipelineOperationId, stageId, host, catalog);
    }

    /// <summary>Re-read authoritative catalog metadata and return the exact requested key.</summary>
    public async Task<FederatedArtifact> InspectAsync(
        FederatedArtifactKey key, IWorkflowHostArtifactSource? host = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        IReadOnlyList<FederatedArtifact> entries = await ListAsync(
            key.PipelineOperationId, key.StageId, host, cancellationToken);
        return entries.SingleOrDefault(a => a.Key == key) ??
            throw new KeyNotFoundException("Artifact no longer belongs to the pinned stage catalog.");
    }

    /// <summary>Fetch a freshly revalidated artifact to a new destination, preserving its exact provenance.</summary>
    public async Task<FederatedArtifactFetchReceipt> FetchAsync(
        FederatedArtifact requested, string destinationPath, IWorkflowHostArtifactSource? host = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        FederatedArtifact current = await InspectAsync(requested.Key, host, cancellationToken);
        if (current != requested || current.Expired || current.Sha256 is not { Length: 64 } ||
            !current.Sha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Pinned artifact catalog metadata changed or lacks a valid digest.");
        }

        (PipelineRunSnapshot pipeline, PipelineStageSnapshot stage) =
            BoundStage(requested.Key.PipelineOperationId, requested.Key.StageId);
        if (stage.Kind == "github.actions")
        {
            GitHubArtifactExtractionReceipt extracted = await RequireGitHubDownloader().FetchAsync(
                pipeline, stage.Id, current, destinationPath, cancellationToken);
            return new(current.Key, extracted.DestinationDirectory, true, extracted.ArchiveSha256,
                extracted.Files);
        }

        RequireHost(stage, host);
        OperationArtifactList raw = await host!.ListAsync(cancellationToken);
        IReadOnlyList<FederatedArtifact> mapped = MapHostCatalog(
            pipeline.OperationId, stage.Id, host, raw);
        OperationArtifactInfo? selected = raw.Artifacts.SingleOrDefault(a => a.ArtifactId == current.Key.ArtifactId);
        if (selected is null || !mapped.Contains(current))
        {
            throw new InvalidDataException("Host catalog changed before download.");
        }
        string path = await host.DownloadAsync(selected, destinationPath, cancellationToken);
        return new(current.Key, path, false, current.Sha256!,
            [new VerifiedArchiveFile(Path.GetFileName(path), selected.Size, current.Sha256!)]);
    }

    /// <summary>Check the files against a saved fetch receipt, without contacting providers.</summary>
    /// <remarks>Verification is local integrity evidence, not authentication of caller-supplied receipts.</remarks>
    public static async Task<bool> VerifyAsync(FederatedArtifactFetchReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.IsDirectory != (receipt.Key.Origin == ArtifactOrigin.GitHubActions) ||
            receipt.Sha256.Length != 64 || !receipt.Sha256.All(char.IsAsciiHexDigit) ||
            receipt.Files.Count > 1024 || (!receipt.IsDirectory && receipt.Files.Count != 1))
        {
            throw new InvalidDataException("Invalid artifact verification receipt.");
        }

        string root = Path.GetFullPath(receipt.Path);
        if (receipt.IsDirectory)
        {
            if (!Directory.Exists(root) || IsLinked(root))
            {
                return false;
            }
            HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase);
            foreach (VerifiedArchiveFile item in receipt.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = ValidRelative(item.RelativePath);
                if (!expected.Add(relative))
                {
                    throw new InvalidDataException("Duplicate artifact receipt paths.");
                }
                string file = Path.GetFullPath(Path.Combine(root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    !SafeComponents(root, relative) ||
                    !await VerifyFileAsync(file, item.Size, item.Sha256, cancellationToken))
                {
                    return false;
                }
            }

            int observed = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++observed > 2048 || IsLinked(path))
                {
                    return false;
                }
                if (File.Exists(path) && !expected.Contains(
                        Path.GetRelativePath(root, path).Replace('\\', '/')))
                {
                    return false;
                }
            }
            return true;
        }

        VerifiedArchiveFile one = receipt.Files.Single();
        return !IsLinked(root) && await VerifyFileAsync(root, one.Size, one.Sha256, cancellationToken) &&
            string.Equals(one.Sha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> VerifyFileAsync(string path, long size, string digest,
        CancellationToken cancellationToken)
    {
        if (size < 0 || digest.Length != 64 || !digest.All(char.IsAsciiHexDigit) ||
            !File.Exists(path) || IsLinked(path))
        {
            return false;
        }
        await using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != size)
        {
            return false;
        }
        string observed = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
        return string.Equals(observed, digest, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SafeComponents(string root, string relative)
    {
        string current = root;
        string[] segments = relative.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (!Directory.Exists(current) || IsLinked(current))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLinked(string path) =>
        (File.Exists(path) || Directory.Exists(path)) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string ValidRelative(string value)
    {
        return string.IsNullOrWhiteSpace(value) || value.Length > 1024 ||
            value.StartsWith('/') || value.Contains(':', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) || value.Any(char.IsControl) ||
            value.Split('/').Any(static s => s is "" or "." or "..")
            ? throw new InvalidDataException("Unsafe receipt-relative path.") : value;
    }

    private (PipelineRunSnapshot Pipeline, PipelineStageSnapshot Stage) BoundStage(
        string pipelineOperationId, string stageId)
    {
        PipelineRunSnapshot pipeline = ledger.Get(pipelineOperationId);
        PipelineStageSnapshot stage = pipeline.Stages.SingleOrDefault(s => s.Id == stageId) ??
            throw new KeyNotFoundException("Unknown pipeline stage.");
        return stage.Status is PipelineStageStates.Succeeded or PipelineStageStates.Failed &&
            stage.Kind is "workflow" or "github.actions"
            ? (pipeline, stage) :
            throw new InvalidOperationException("Artifact catalog requires a finalized bound stage.");
    }

    private static void RequireHost(PipelineStageSnapshot stage, IWorkflowHostArtifactSource? host)
    {
        if (stage.Kind != "workflow" || host is null ||
            host.Origin is not (ArtifactOrigin.HostLocal or ArtifactOrigin.HostSsh) ||
            string.IsNullOrWhiteSpace(host.HostIdentity) || host.HostIdentity.Length > 1024 ||
            string.IsNullOrWhiteSpace(stage.ChildOperationId) ||
            !string.Equals(stage.ChildOperationId, host.OperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Host source does not match the durable child operation.");
        }
    }

    private static ReadOnlyCollection<FederatedArtifact> MapHostCatalog(string pipelineOperationId,
        string stageId, IWorkflowHostArtifactSource host, OperationArtifactList catalog)
    {
        if (catalog.OperationId != host.OperationId ||
            catalog.ReportSha256 is not { Length: 64 } || !catalog.ReportSha256.All(char.IsAsciiHexDigit) ||
            catalog.Artifacts is null || catalog.Artifacts.Count > 512)
        {
            throw new InvalidDataException("Invalid finalized Host artifact catalog.");
        }
        string source = "host:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(host.HostIdentity))) + ":" + host.OperationId;
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<FederatedArtifact> entries = [];
        foreach (OperationArtifactInfo item in catalog.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(item.ArtifactId) || !seen.Add(item.ArtifactId) ||
                item.Size < 0 || item.LastWriteUtcTicks < 0 || item.Sha256 is not { Length: 64 } ||
                !item.Sha256.All(char.IsAsciiHexDigit))
            {
                throw new InvalidDataException("Invalid or duplicated Host artifact identity.");
            }
            entries.Add(new(new FederatedArtifactKey(host.Origin, pipelineOperationId, stageId,
                source, item.ArtifactId), item.RelativePath, item.Size,
                item.Sha256.ToUpperInvariant(), ArtifactDigestEvidence.ProviderReported,
                ArtifactExecutionBinding.ExactOperation, false, null));
        }
        return entries.AsReadOnly();
    }

    private GitHubActionsArtifactCatalog RequireGitHubCatalog() => githubCatalog ??
        throw new InvalidOperationException("GitHub artifact catalog is not configured.");

    private GitHubActionsArtifactDownloader RequireGitHubDownloader() => githubDownloader ??
        throw new InvalidOperationException("GitHub artifact downloader is not configured.");
}
