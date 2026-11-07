using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CMakeWorkflow.Pipelines.Artifacts;
using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>Bounded GitHub Actions artifact metadata reader; never downloads, retries, reruns or dispatches.</summary>
/// <remarks>
/// GitHub's run-artifacts endpoint is scoped to a run ID, not a run attempt. Its artifact records carry
/// workflow_run.id and head_sha but no trustworthy run_attempt. All observations are explicitly RunOnly,
/// even after this reader independently verifies the requested attempt through IGitHubActionsRunReader.
/// The digest is provider-reported and MUST NOT be mistaken for a verified local file hash.
/// HttpClient belongs to the caller; token is used only for GitHub API requests.
/// </remarks>
public sealed class GitHubActionsArtifactCatalog(
    HttpClient client, IGitHubActionsRunReader attemptReader, string? token = null)
{
    private const int PageSize = 100;
    private const int MaximumEntries = 512;
    private const int MaximumPageBytes = 1024 * 1024;
    private readonly HttpClient client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly IGitHubActionsRunReader attemptReader = attemptReader ??
        throw new ArgumentNullException(nameof(attemptReader));
    private readonly string? token = token;

    /// <summary>Read the terminal attached stage in an admitted immutable pipeline snapshot.</summary>
    /// <remarks>Dispatch stages use their persisted run ID/attempt binding; nothing is re-dispatched.</remarks>
    public async Task<IReadOnlyList<FederatedArtifact>> ListAsync(
        PipelineRunSnapshot pipeline, string stageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (string.IsNullOrWhiteSpace(pipeline.OperationId) || pipeline.OperationId.Length > 128 ||
            string.IsNullOrWhiteSpace(stageId) || stageId.Length > 128)
        {
            throw new ArgumentException("Invalid pipeline operation or stage identity.");
        }

        PipelineStageSnapshot snapshot = pipeline.Stages.SingleOrDefault(s => s.Id == stageId)
            ?? throw new ArgumentException("Unknown pipeline stage.", nameof(stageId));
        if (snapshot.Kind != "github.actions" ||
            snapshot.Status is not (PipelineStageStates.Succeeded or PipelineStageStates.Failed))
        {
            throw new InvalidOperationException("GitHub artifact listing requires a terminal stage.");
        }

        PipelineDocument frozen = PipelineManifest.ParseJson(pipeline.ManifestJson);
        _ = PipelinePlanner.Plan(frozen);
        PipelineGitHubActionsStage admitted = frozen.Stages.Single(s => s.Id == stageId).GitHubActions
            ?? throw new InvalidDataException("Terminal GitHub stage lacks its admitted provider identity.");
        PipelineGitHubActionsStage pinned = admitted.Mode == "attach" ? admitted :
            PinDispatched(admitted, snapshot);

        string reference = "github:" + pinned.Repository + ":" + pinned.Workflow + ":" +
            pinned.RunId.ToString(CultureInfo.InvariantCulture) + ":" +
            pinned.RunAttempt.ToString(CultureInfo.InvariantCulture) + ":" + pinned.ExpectedHeadSha;
        string evidence = "github-attempt:" + pinned.Repository + ":" +
            pinned.RunId.ToString(CultureInfo.InvariantCulture) + ":" +
            pinned.RunAttempt.ToString(CultureInfo.InvariantCulture) + ":sha256:";
        if (!string.Equals(snapshot.ExternalReference, reference, StringComparison.Ordinal) ||
            snapshot.EvidenceReference is not { Length: > 0 } ||
            !snapshot.EvidenceReference.StartsWith(evidence, StringComparison.Ordinal) ||
            snapshot.EvidenceReference.Length != evidence.Length + 64 ||
            !snapshot.EvidenceReference[evidence.Length..].All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Terminal stage lacks matching durable provider evidence.");
        }

        GitHubRunAttempt attempt = await attemptReader.GetAttemptAsync(pinned, cancellationToken);
        GitHubActionsStageWatcher.Verify(pinned, attempt);
        if (admitted.Mode == "dispatch" &&
            (string.IsNullOrWhiteSpace(snapshot.ChildRequestId) ||
             attempt.Event != "workflow_dispatch" ||
             attempt.DisplayTitle != "cmwf-" + snapshot.ChildRequestId))
        {
            throw new InvalidDataException("Dispatch artifact run lacks its durable correlation marker.");
        }
        if (attempt.Status != "completed")
        {
            throw new InvalidOperationException("Artifact catalog requires a completed pinned attempt.");
        }

        List<FederatedArtifact> artifacts = [];
        HashSet<long> ids = [];
        int? total = null;
        for (int page = 1; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using HttpRequestMessage request = new(HttpMethod.Get,
                "https://api.github.com/repos/" + pinned.Repository + "/actions/runs/" +
                pinned.RunId.ToString(CultureInfo.InvariantCulture) + "/artifacts?per_page=" +
                PageSize.ToString(CultureInfo.InvariantCulture) + "&page=" +
                page.ToString(CultureInfo.InvariantCulture));
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
            if (response.Content.Headers.ContentLength > MaximumPageBytes)
            {
                throw new InvalidDataException("GitHub artifact catalog response exceeds bounded size.");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using MemoryStream buffer = new();
            byte[] chunk = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(chunk, cancellationToken);
                if (count == 0)
                {
                    break;
                }
                if (buffer.Length + count > MaximumPageBytes)
                {
                    throw new InvalidDataException("GitHub artifact catalog response exceeds bounded size.");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
            }

            using JsonDocument parsed = JsonDocument.Parse(buffer.ToArray());
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("total_count", out JsonElement totalElement) ||
                totalElement.ValueKind != JsonValueKind.Number ||
                !totalElement.TryGetInt32(out int countTotal) ||
                countTotal is < 0 or > MaximumEntries ||
                !root.TryGetProperty("artifacts", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > PageSize ||
                (total.HasValue && countTotal != total.Value))
            {
                throw new InvalidDataException("Invalid, oversized or changed GitHub artifact catalog.");
            }

            total = countTotal;
            foreach (JsonElement item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("GitHub artifact entry is not an object.");
                }
                long id = RequiredPositiveInt(item, "id");
                JsonElement run = RequiredObject(item, "workflow_run");
                if (!ids.Add(id) || RequiredPositiveInt(run, "id") != pinned.RunId ||
                    !string.Equals(RequiredString(run, "head_sha"), pinned.ExpectedHeadSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("GitHub artifact identity mismatches the pinned run.");
                }

                string name = RequiredString(item, "name");
                if (name.Length > 255 || name.Any(char.IsControl))
                {
                    throw new InvalidDataException("GitHub artifact name exceeds limits.");
                }
                long size = RequiredNonnegativeInt(item, "size_in_bytes");
                string? digest = Digest(item);
                if (!item.TryGetProperty("expired", out JsonElement expiredElement) ||
                    expiredElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException("Invalid GitHub artifact expired state.");
                }
                bool expired = expiredElement.GetBoolean();
                DateTimeOffset? expiresAt = Expiration(item);
                FederatedArtifactKey key = new(ArtifactOrigin.GitHubActions, pipeline.OperationId, stageId,
                    "github:" + pinned.Repository.ToUpperInvariant() + ":" +
                    pinned.RunId.ToString(CultureInfo.InvariantCulture),
                    id.ToString(CultureInfo.InvariantCulture));
                artifacts.Add(new(key, name, size, digest,
                    digest is null ? ArtifactDigestEvidence.NotPublished : ArtifactDigestEvidence.ProviderReported,
                    ArtifactExecutionBinding.RunOnly, expired, expiresAt));
            }

            if (artifacts.Count > total.Value)
            {
                throw new InvalidDataException("GitHub artifact pagination exceeded provider total.");
            }
            if (artifacts.Count == total.Value)
            {
                return artifacts.AsReadOnly();
            }
            if (entries.GetArrayLength() != PageSize || page >= 6)
            {
                throw new InvalidDataException("GitHub artifact pagination is missing or inconsistent.");
            }
        }
    }

    private static PipelineGitHubActionsStage PinDispatched(PipelineGitHubActionsStage admitted,
        PipelineStageSnapshot snapshot)
    {
        string? binding = snapshot.ExternalReference;
        string prefix = "github:" + admitted.Repository + ":" + admitted.Workflow + ":";
        string suffix = ":1:" + admitted.ExpectedHeadSha;
        if (binding is null || !binding.StartsWith(prefix, StringComparison.Ordinal) ||
            !binding.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("GitHub dispatch artifact has no matching durable run binding.");
        }

        string number = binding[prefix.Length..^suffix.Length];
        long runId = long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) &&
            parsed > 0 ? parsed : throw new InvalidDataException("Invalid durable GitHub dispatch run ID.");

        return new PipelineGitHubActionsStage
        {
            Mode = "attach",
            Repository = admitted.Repository,
            Workflow = admitted.Workflow,
            ExpectedHeadSha = admitted.ExpectedHeadSha,
            RunId = runId,
            RunAttempt = 1
        };
    }

    private static JsonElement RequiredObject(JsonElement parent, string key)
    {
        JsonElement value = parent.GetProperty(key);
        return value.ValueKind == JsonValueKind.Object ? value :
            throw new InvalidDataException("GitHub artifact field must be an object: " + key);
    }

    private static string RequiredString(JsonElement parent, string key)
    {
        JsonElement value = parent.GetProperty(key);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ?
            value.GetString()! : throw new InvalidDataException("GitHub artifact field must be text: " + key);
    }

    private static long RequiredPositiveInt(JsonElement parent, string key)
    {
        JsonElement value = parent.GetProperty(key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number > 0 ?
            number : throw new InvalidDataException("GitHub artifact ID must be positive: " + key);
    }

    private static long RequiredNonnegativeInt(JsonElement parent, string key)
    {
        JsonElement value = parent.GetProperty(key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number >= 0 ?
            number : throw new InvalidDataException("GitHub artifact size must be nonnegative: " + key);
    }

    private static string? Digest(JsonElement item)
    {
        if (!item.TryGetProperty("digest", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("GitHub artifact digest must be text.");
        }
        string? raw = value.GetString();
        return raw is null || !raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            raw.Length != 71 || !raw[7..].All(char.IsAsciiHexDigit)
            ? throw new InvalidDataException("Unsupported or malformed GitHub artifact digest.")
            : raw[7..].ToUpperInvariant();
    }

    private static DateTimeOffset? Expiration(JsonElement item)
    {
        if (!item.TryGetProperty("expires_at", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Invalid artifact expiry timestamp.");
        }
        string? timestamp = value.GetString();
        return timestamp is null || !timestamp.EndsWith('Z') ||
            !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out DateTimeOffset date)
            ? throw new InvalidDataException("Invalid artifact expiry timestamp.")
            : date;
    }
}
