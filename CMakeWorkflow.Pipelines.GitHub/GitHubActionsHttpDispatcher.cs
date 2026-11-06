using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>One-shot GitHub workflow_dispatch client using the 200/run-id response introduced in 2026.</summary>
/// <remarks>
/// Borrows HttpClient and write-scoped token; never retries HTTP requests. A 204, timeout, malformed
/// response, or lost connection is an ambiguous outcome and must leave durable dispatch_intent unchanged.
/// </remarks>
public sealed class GitHubActionsHttpDispatcher(HttpClient client, string token) : IGitHubActionsDispatcher
{
    private readonly HttpClient client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly string token = !string.IsNullOrWhiteSpace(token) ? token :
        throw new ArgumentException("GitHub Actions write token is required.", nameof(token));

    public async Task<GitHubDispatchReceipt> DispatchOnceAsync(PipelineGitHubActionsStage stage,
        string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Mode != "dispatch" || string.IsNullOrWhiteSpace(correlationId) ||
            correlationId.Length > 128 || !correlationId.All(static c =>
                char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.'))
        {
            throw new ArgumentException("Expected a pinned GitHub dispatch and correlation ID.", nameof(stage));
        }

        // Validate the full request before any network-side effect.
        _ = PipelinePlanner.Plan(new PipelineDocument
        {
            SchemaVersion = 1,
            Name = "dispatch",
            Stages = [new PipelineStage { Id = "dispatch", Kind = "github.actions", GitHubActions = stage }]
        });

        string workflow = stage.Workflow.StartsWith(".github/workflows/", StringComparison.Ordinal)
            ? stage.Workflow[".github/workflows/".Length..] : stage.Workflow;
        if (workflow.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("GitHub workflow dispatch requires a workflow file name.", nameof(stage));
        }

        string uri = "https://api.github.com/repos/" + stage.Repository + "/actions/workflows/" +
            Uri.EscapeDataString(workflow) + "/dispatches";
        using HttpRequestMessage request = new(HttpMethod.Post, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("CMakeWorkflow-Pipelines/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using MemoryStream payload = new();
        using (Utf8JsonWriter json = new(payload))
        {
            json.WriteStartObject();
            json.WriteString("ref", stage.Ref);
            json.WriteBoolean("return_run_details", true);
            json.WritePropertyName("inputs");
            json.WriteStartObject();
            foreach (KeyValuePair<string, string> input in stage.Inputs ?? new Dictionary<string, string>())
            {
                json.WriteString(input.Key, input.Value);
            }

            json.WriteString("cmwf_request_id", correlationId);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        using ByteArrayContent content = new(payload.ToArray());
        request.Content = content;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            // 204 can mean that dispatch happened, but it contains no durable run identity.
            _ = response.EnsureSuccessStatusCode();
            throw new InvalidDataException("GitHub dispatch returned no run ID; outcome is unknown.");
        }

        const int limit = 64 * 1024;
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new InvalidDataException("GitHub dispatch receipt is too large.");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[4096];
        while (true)
        {
            int count = await body.ReadAsync(chunk, cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (buffer.Length + count > limit)
            {
                throw new InvalidDataException("GitHub dispatch receipt is too large.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }

        using JsonDocument parsed = JsonDocument.Parse(buffer.ToArray());
        JsonElement receipt = parsed.RootElement;
        if (receipt.ValueKind != JsonValueKind.Object ||
            !receipt.TryGetProperty("workflow_run_id", out JsonElement id) ||
            id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long runId) || runId <= 0)
        {
            throw new InvalidDataException("GitHub dispatch receipt lacks a valid workflow_run_id.");
        }

        string expectedApi = "https://api.github.com/repos/" + stage.Repository + "/actions/runs/" +
            runId.ToString(CultureInfo.InvariantCulture);
        string expectedWeb = "https://github.com/" + stage.Repository + "/actions/runs/" +
            runId.ToString(CultureInfo.InvariantCulture);
        return MatchingUrl(receipt, "run_url", expectedApi) && MatchingUrl(receipt, "html_url", expectedWeb)
            ? new GitHubDispatchReceipt(runId)
            : throw new InvalidDataException("GitHub dispatch receipt URLs do not match pinned repository and run ID.");
    }

    private static bool MatchingUrl(JsonElement receipt, string key, string expected) =>
        receipt.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);
}
