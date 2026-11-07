using System.Globalization;
using System.Net;
using CMakeWorkflow.Pipelines.Artifacts;
using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>Fetch a single pinned GitHub Actions artifact and publish only verified ZIP contents.</summary>
/// <remarks>
/// The caller supplies an immutable pipeline snapshot from its durable ledger. Catalog metadata is re-read
/// from the pinned run before issuing a download; an artifact ID/name alone never grants download authority.
/// GitHub's artifact-to-run binding does not prove which run attempt uploaded the ZIP: the returned receipt
/// preserves RunOnly provenance. A failed fetch never modifies the pipeline ledger or retries a workflow.
/// Neither redirects nor ZIP bytes are cached; explicit retries obtain a new short-lived redirect.
/// </remarks>
public sealed class GitHubActionsArtifactDownloader(
    GitHubActionsArtifactCatalog catalog, IGitHubArtifactHttpTransport transport, string? token = null)
{
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private readonly GitHubActionsArtifactCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IGitHubArtifactHttpTransport transport = transport ??
        throw new ArgumentNullException(nameof(transport));
    private readonly string? token = token;

    public async Task<GitHubArtifactExtractionReceipt> FetchAsync(
        PipelineRunSnapshot pipeline, string stageId, FederatedArtifact requested,
        string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        // The catalog authenticates the pinned run, stage evidence and observed artifact identity.
        // Its run-only provenance must not be silently promoted to an attempt-specific binding.
        IReadOnlyList<FederatedArtifact> candidates = await catalog.ListAsync(pipeline, stageId, cancellationToken);
        FederatedArtifact current = candidates.SingleOrDefault(a => a.Key == requested.Key) ??
            throw new InvalidDataException("Artifact is not in the freshly verified run catalog.");
        if (current != requested || current.Expired ||
            current.DigestEvidence != ArtifactDigestEvidence.ProviderReported ||
            current.ExecutionBinding != ArtifactExecutionBinding.RunOnly ||
            current.Sha256 is not { Length: 64 } || !current.Sha256.All(char.IsAsciiHexDigit) ||
            current.Size is null || current.Size.Value is < 0 or > MaximumArchiveBytes)
        {
            throw new InvalidDataException("Artifact catalog identity, digest or availability changed.");
        }

        (string repository, long runId) = RequireSource(current.Key.SourceIdentity);
        if (!long.TryParse(current.Key.ArtifactId, NumberStyles.None, CultureInfo.InvariantCulture,
                out long artifactId) || artifactId <= 0 ||
            artifactId.ToString(CultureInfo.InvariantCulture) != current.Key.ArtifactId)
        {
            throw new InvalidDataException("Invalid pinned GitHub artifact ID.");
        }

        // Rechecking this against the catalog-derived source prevents a caller-supplied URL override.
        if (current.Key.Origin != ArtifactOrigin.GitHubActions || runId <= 0)
        {
            throw new InvalidDataException("Unsupported artifact origin or run identity.");
        }

        Uri apiUri = new("https://api.github.com/repos/" + repository + "/actions/artifacts/" +
            artifactId.ToString(CultureInfo.InvariantCulture) + "/zip");
        using HttpResponseMessage api = await transport.GetApiAsync(apiUri, token, cancellationToken);
        if (api.StatusCode == HttpStatusCode.Gone)
        {
            throw new InvalidOperationException("The GitHub artifact has expired or is no longer available.");
        }
        if (api.StatusCode != HttpStatusCode.Redirect)
        {
            _ = api.EnsureSuccessStatusCode();
            throw new InvalidDataException("GitHub artifact API did not return the required 302 redirect.");
        }

        Uri signedUrl = api.Headers.Location ??
            throw new InvalidDataException("GitHub artifact response has no redirect location.");
        RequireStorageUri(signedUrl);

        // A second, authorization-free HTTP hop. No automatic redirect, no secret-bearing headers.
        using HttpResponseMessage download = await transport.GetStorageAsync(signedUrl, cancellationToken);
        if (download.StatusCode != HttpStatusCode.OK)
        {
            // Storage is a terminal hop: an unexpected redirect must not trigger
            // another request, and is a protocol violation rather than an HTTP failure.
            if ((int)download.StatusCode is >= 300 and < 400)
            {
                throw new InvalidDataException("Artifact storage returned an unexpected redirect.");
            }

            _ = download.EnsureSuccessStatusCode();
            throw new InvalidDataException("Artifact storage did not return an archive body.");
        }
        if (download.Content.Headers.ContentLength > MaximumArchiveBytes)
        {
            throw new InvalidDataException("Artifact storage response exceeds ZIP limit.");
        }

        await using Stream stream = await download.Content.ReadAsStreamAsync(cancellationToken);
        return await GitHubArtifactArchiveVerifier.ExtractAsync(current, stream,
            destinationDirectory, cancellationToken);
    }

    /// <summary>Reject storage redirects outside known HTTPS GitHub Actions / Azure blob endpoints.</summary>
    /// <remarks>The allowlist is intentional; add provider domains only with a documented security review.</remarks>
    public static void RequireStorageUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.HostNameType != UriHostNameType.Dns || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 ||
            !(uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".actions.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            // Never include a signed URL in exceptions: its query may contain a bearer signature.
            throw new InvalidDataException("Untrusted GitHub artifact storage redirect destination.");
        }
    }

    private static (string Repository, long RunId) RequireSource(string source)
    {
        string[] components = source.Split(':');
        if (components.Length != 3 || components[0] != "github" ||
            !long.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture,
                out long runId) || runId <= 0 ||
            runId.ToString(CultureInfo.InvariantCulture) != components[2])
        {
            throw new InvalidDataException("Invalid catalog GitHub source identity.");
        }
        string[] repo = components[1].Split('/');
        return repo.Length != 2 || repo.Any(static part => part.Length is < 1 or > 128 ||
            part is "." or ".." || part.Any(static ch =>
                !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-' or '.')))
            ? throw new InvalidDataException("Invalid catalog GitHub repository identity.")
            : (components[1], runId);
    }
}
