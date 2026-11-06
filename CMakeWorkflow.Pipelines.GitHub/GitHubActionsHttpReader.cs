using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>Bounded, read-only GitHub REST adapter for a particular workflow run attempt.</summary>
/// <remarks>
/// Borrows HttpClient. Supply a read-only Actions token for private repositories; no credential is read implicitly.
/// URLs are constructed exclusively under api.github.com from validated owner/repo and numeric IDs.
/// HTTP errors, rate limits, cancellation and malformed payloads propagate without synthetic success or retry.
/// </remarks>
public sealed class GitHubActionsHttpReader(HttpClient client, string? token = null) : IGitHubActionsRunReader
{
    private const int MaxPayloadBytes = 1024 * 1024;
    private readonly HttpClient client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly string? token = token;

    public async Task<GitHubRunAttempt> GetAttemptAsync(PipelineGitHubActionsStage expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!SafeRepository(expected.Repository) || expected.RunId <= 0 || expected.RunAttempt <= 0)
        {
            throw new ArgumentException("Invalid pinned GitHub API identity.", nameof(expected));
        }

        string uri = "https://api.github.com/repos/" + expected.Repository + "/actions/runs/" +
            expected.RunId.ToString(CultureInfo.InvariantCulture) + "/attempts/" +
            expected.RunAttempt.ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("CMakeWorkflow-Pipelines/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        _ = response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPayloadBytes)
        {
            throw new InvalidDataException("GitHub response exceeds bounded maximum.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxPayloadBytes)
            {
                throw new InvalidDataException("GitHub response exceeds bounded maximum.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        byte[] body = buffer.ToArray();
        using JsonDocument parsed = JsonDocument.Parse(body);
        JsonElement root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("GitHub run attempt must be a JSON object.");
        }

        long id = Number(root, "id");
        long attempt = Number(root, "run_attempt");
        if (attempt is < 1 or > int.MaxValue)
        {
            throw new InvalidDataException("Invalid GitHub run attempt number.");
        }

        JsonElement repository = Object(root, "repository");
        string path = Text(root, "path");
        int refIndex = path.LastIndexOf('@');
        if (refIndex > 0)
        {
            path = path[..refIndex];
        }

        JsonElement conclusion = root.GetProperty("conclusion");
        return conclusion.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
            ? throw new InvalidDataException("Invalid GitHub run conclusion.")
            : new GitHubRunAttempt(id, (int)attempt, Text(repository, "full_name"), path,
            Text(root, "head_sha"), Text(root, "status"),
            conclusion.ValueKind == JsonValueKind.Null ? null : conclusion.GetString(),
            Convert.ToHexString(SHA256.HashData(body)));
    }

    private static JsonElement Object(JsonElement parent, string property)
    {
        JsonElement child = parent.GetProperty(property);
        return child.ValueKind == JsonValueKind.Object ? child :
            throw new InvalidDataException($"GitHub field '{property}' must be an object.");
    }

    private static string Text(JsonElement parent, string property)
    {
        JsonElement child = parent.GetProperty(property);
        return child.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(child.GetString())
            ? child.GetString()! : throw new InvalidDataException($"GitHub field '{property}' must be a nonempty string.");
    }

    private static long Number(JsonElement parent, string property)
    {
        JsonElement child = parent.GetProperty(property);
        return child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out long value)
            ? value : throw new InvalidDataException($"GitHub field '{property}' must be an integer.");
    }

    private static bool SafeRepository(string? value)
    {
        string[] parts = (value ?? "").Split('/');
        return parts.Length == 2 && parts.All(static part =>
            part.Length is > 0 and <= 128 && part is not ("." or "..") &&
            part.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'));
    }
}
