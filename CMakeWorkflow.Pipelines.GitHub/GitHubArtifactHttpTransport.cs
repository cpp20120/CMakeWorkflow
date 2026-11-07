using System.Net.Http.Headers;

namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>A single explicit HTTP hop; returned response and content are owned by the caller.</summary>
/// <remarks>
/// Implementations must not automatically follow redirects. The API hop may receive a GitHub token;
/// the storage hop must never receive that token or any other GitHub authorization headers.
/// This interface also permits deterministic offline tests without fetching real artifacts.
/// </remarks>
public interface IGitHubArtifactHttpTransport
{
    Task<HttpResponseMessage> GetApiAsync(Uri uri, string? token, CancellationToken cancellationToken);

    Task<HttpResponseMessage> GetStorageAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>Production no-cookie, no-auto-redirect transport for GitHub artifact downloads.</summary>
/// <remarks>
/// Authorization is assigned per API request, never to HttpClient.DefaultRequestHeaders.
/// Storage requests use a separate message without bearer credentials. The caller validates the
/// storage destination before invoking this transport. The redirect URL may itself contain a
/// short-lived bearer signature; never log its path or query string.
/// </remarks>
public sealed class GitHubArtifactHttpTransport : IGitHubArtifactHttpTransport, IDisposable
{
    private readonly SocketsHttpHandler handler = new()
    {
        AllowAutoRedirect = false,
        UseCookies = false
    };

    private readonly HttpClient client;

    public GitHubArtifactHttpTransport()
    {
        client = new HttpClient(handler, disposeHandler: false);
    }

    public Task<HttpResponseMessage> GetApiAsync(Uri uri, string? token,
        CancellationToken cancellationToken)
    {
        return uri is null || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            ? throw new ArgumentException("Artifact API URL must be on api.github.com.", nameof(uri))
            : SendAsync(uri, token, cancellationToken);
    }

    public Task<HttpResponseMessage> GetStorageAsync(Uri uri, CancellationToken cancellationToken)
    {
        GitHubActionsArtifactDownloader.RequireStorageUri(uri);
        return SendAsync(uri, null, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string? token,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        if (string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("CMakeWorkflow-Pipelines/1.0");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        // In particular: do not send GitHub credentials, API user-agent or cookies to blob storage.
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public void Dispose()
    {
        client.Dispose();
        handler.Dispose();
    }
}
