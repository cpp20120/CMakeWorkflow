using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Pipelines;
using CMakeWorkflow.Pipelines.Artifacts;
using CMakeWorkflow.Pipelines.GitHub;
using CMakeWorkflow.Pipelines.Persistence;

internal static class GitHubActionsArtifactCatalogTests
{
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string ArtifactSha = new('A', 64);

    internal static async Task RunAsync()
    {
        await CatalogAndIdentityAsync();
        await DispatchedArtifactBindingAsync();
        await PaginationAsync();
        await RefuseRunMismatchAsync();
        await RefusePaginationAndDigestAsync();
        await RefuseInvalidAttemptAndNetworkAsync();
        Console.WriteLine("P5a GitHub artifact catalog: exact run, bounded pagination, digest and attempt provenance passed.");
    }

    private static PipelineGitHubActionsStage Pinned() => new()
    {
        Mode = "attach",
        Repository = "cpp20120/CMakeWorkflow",
        Workflow = "ci.yml",
        RunId = 99,
        RunAttempt = 2,
        ExpectedHeadSha = CommitSha
    };

    private static PipelineRunSnapshot Snapshot(string stageStatus = PipelineStageStates.Succeeded,
        string? externalReference = null, bool dispatched = false) => new(
            "pipeline-1", "client-request", "test", new string('C', 64),
            PipelineManifest.ToJson(new PipelineDocument
            {
                SchemaVersion = 1,
                Name = "test",
                Stages = [new PipelineStage
                {
                    Id = "ci", Kind = "github.actions", GitHubActions = dispatched ? new PipelineGitHubActionsStage
                    {
                        Mode = "dispatch", Repository = "cpp20120/CMakeWorkflow", Workflow = "ci.yml",
                        Ref = "refs/heads/main", ExpectedHeadSha = CommitSha
                    } : Pinned()
                }]
            }),
            2, stageStatus,
            [new PipelineStageSnapshot("ci", "github.actions", stageStatus, 2,
                dispatched ? "corr-1" : null, null,
                externalReference ?? ExternalReference(dispatched),
                "github-attempt:cpp20120/CMakeWorkflow:99:" +
                    (dispatched ? "1:sha256:" : "2:sha256:") + new string('B', 64))]);

    private static string ExternalReference(bool dispatched) =>
        "github:cpp20120/CMakeWorkflow:ci.yml:99:" + (dispatched ? "1:" : "2:") + CommitSha;

    private static GitHubRunAttempt Attempt(int number = 2, string status = "completed") =>
        new(99, number, "cpp20120/CMakeWorkflow", ".github/workflows/ci.yml",
            CommitSha, status, status == "completed" ? "success" : null, new string('B', 64));

    private static string Item(long id, string? digest = null, long runId = 99,
        string sha = CommitSha, bool expired = false) =>
        $$"""
          {
            "id": {{id.ToString(CultureInfo.InvariantCulture)}},
            "name": "artifact-{{id.ToString(CultureInfo.InvariantCulture)}}",
            "size_in_bytes": 42,
            "expired": {{(expired ? "true" : "false")}},
            "expires_at": "2026-12-01T00:00:00Z",
            "digest": {{(digest is null ? "null" : "\"sha256:" + digest + "\"")}},
            "workflow_run": {"id": {{runId.ToString(CultureInfo.InvariantCulture)}}, "head_sha": "{{sha}}"}
          }
          """;

    private static string Page(int total, params string[] items) =>
        "{\"total_count\":" + total + ",\"artifacts\":[" + string.Join(",", items) + "]}";

    private static async Task CatalogAndIdentityAsync()
    {
        using StubHandler handler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Page(2, Item(12, ArtifactSha), Item(13, expired: true)),
                    Encoding.UTF8, "application/json")
            });
        using HttpClient http = new(handler, disposeHandler: false);
        GitHubActionsArtifactCatalog catalog = new(http, new StubReader(Attempt()));
        IReadOnlyList<FederatedArtifact> list = await catalog.ListAsync(Snapshot(), "ci");
        Check(list.Count == 2 && list[0].Key.ArtifactId == "12" && list[1].Expired,
            "Artifacts not enumerated correctly.");
        Check(list[0].Key.Origin == ArtifactOrigin.GitHubActions &&
              list[0].ExecutionBinding == ArtifactExecutionBinding.RunOnly &&
              list[0].Key.SourceIdentity == "github:CPP20120/CMAKEWORKFLOW:99" &&
              list[0].Sha256 == ArtifactSha &&
              list[0].DigestEvidence == ArtifactDigestEvidence.ProviderReported &&
              list[1].DigestEvidence == ArtifactDigestEvidence.NotPublished,
            "Artifact was assigned a false attempt-bound or verified-byte identity.");
    }

    private static async Task DispatchedArtifactBindingAsync()
    {
        GitHubRunAttempt run = Attempt(number: 1) with
        {
            Event = "workflow_dispatch",
            DisplayTitle = "cmwf-corr-1"
        };
        using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Page(1, Item(24)), Encoding.UTF8, "application/json")
        });
        using HttpClient http = new(handler, disposeHandler: false);
        GitHubActionsArtifactCatalog catalog = new(http, new StubReader(run));
        IReadOnlyList<FederatedArtifact> artifacts = await catalog.ListAsync(Snapshot(dispatched: true), "ci");
        Check(artifacts.Count == 1 && artifacts[0].ExecutionBinding == ArtifactExecutionBinding.RunOnly,
            "Dispatch artifact must bind to frozen run but not claim its attempt as proven.");

        GitHubActionsArtifactCatalog wrongMarker = new(http, new StubReader(run with
        {
            DisplayTitle = "cmwf-other"
        }));
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await wrongMarker.ListAsync(Snapshot(dispatched: true), "ci"); });
    }

    private static async Task PaginationAsync()
    {
        string first = Page(101, [.. Enumerable.Range(1, 100).Select(n => Item(n))]);
        string second = Page(101, Item(101));
        using StubHandler handler = new(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Query.EndsWith("page=1", StringComparison.Ordinal)
                ? first : second, Encoding.UTF8, "application/json")
        });
        using HttpClient http = new(handler, disposeHandler: false);
        GitHubActionsArtifactCatalog catalog = new(http, new StubReader(Attempt()));
        IReadOnlyList<FederatedArtifact> result = await catalog.ListAsync(Snapshot(), "ci");
        Check(handler.Requests == 2 && result.Count == 101 && result[100].Key.ArtifactId == "101",
            "Multi-page catalog omitted or duplicated entries.");
    }

    private static async Task RefuseRunMismatchAsync()
    {
        foreach (string bad in new[]
        {
            Page(1, Item(12, runId: 100)),
            Page(1, Item(12, sha: new string('F', 40))),
            Page(2, Item(12)),
            Page(2, Item(12), Item(12))
        })
        {
            using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bad, Encoding.UTF8, "application/json")
            });
            using HttpClient http = new(handler, disposeHandler: false);
            GitHubActionsArtifactCatalog catalog = new(http, new StubReader(Attempt()));
            await RefusedAsync<InvalidDataException>(async () =>
                { _ = await catalog.ListAsync(Snapshot(), "ci"); });
        }
    }

    private static async Task RefusePaginationAndDigestAsync()
    {
        foreach (string bad in new[]
        {
            Page(1, Item(12, "not-a-digest")),
            Page(513),
            JsonSerializer.Serialize(new { total_count = 1, artifacts = new { } })
        })
        {
            using StubHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bad, Encoding.UTF8, "application/json")
            });
            using HttpClient http = new(handler, disposeHandler: false);
            GitHubActionsArtifactCatalog catalog = new(http, new StubReader(Attempt()));
            await RefusedAsync<InvalidDataException>(async () =>
                { _ = await catalog.ListAsync(Snapshot(), "ci"); });
        }

        using StubHandler malformedHandler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Page(1, Item(12, ArtifactSha)) + "garbage", Encoding.UTF8,
                "application/json")
        });
        using HttpClient malformed = new(malformedHandler, disposeHandler: false);
        GitHubActionsArtifactCatalog malformedCatalog = new(malformed, new StubReader(Attempt()));
        await RefusedAsync<JsonException>(async () =>
            { _ = await malformedCatalog.ListAsync(Snapshot(), "ci"); });
    }

    private static async Task RefuseInvalidAttemptAndNetworkAsync()
    {
        using StubHandler never = new(_ => throw new UnreachableException("Attempt identity must precede catalog HTTP."));
        using (HttpClient http = new(never, disposeHandler: false))
        {
            GitHubActionsArtifactCatalog catalog = new(http, new StubReader(Attempt(3)));
            await RefusedAsync<InvalidDataException>(async () =>
                { _ = await catalog.ListAsync(Snapshot(), "ci"); });
            Check(never.Requests == 0, "Mismatched attempt was followed by a provider request.");
        }

        using StubHandler noBinding = new(_ => throw new UnreachableException("Bad stage binding reached GitHub."));
        using (HttpClient http = new(noBinding, disposeHandler: false))
        {
            GitHubActionsArtifactCatalog catalog = new(http, new StubReader(Attempt()));
            await RefusedAsync<InvalidDataException>(async () =>
                { _ = await catalog.ListAsync(Snapshot(externalReference: "github:wrong"), "ci"); });
            Check(noBinding.Requests == 0, "Tampered stage binding reached GitHub.");
        }

        using StubHandler failingHandler = new(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using HttpClient failing = new(failingHandler, disposeHandler: false);
        GitHubActionsArtifactCatalog provider = new(failing, new StubReader(Attempt()));
        await RefusedAsync<HttpRequestException>(async () =>
            { _ = await provider.ListAsync(Snapshot(), "ci"); });
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition)
        {
            throw new UnreachableException(detail);
        }
    }

    private static async Task RefusedAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new UnreachableException("Expected: " + typeof(T).Name);
    }

    private sealed class StubReader(GitHubRunAttempt attempt) : IGitHubActionsRunReader
    {
        public Task<GitHubRunAttempt> GetAttemptAsync(PipelineGitHubActionsStage expected,
            CancellationToken cancellationToken = default) => Task.FromResult(attempt);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            Check(request.Method == HttpMethod.Get &&
                  (request.RequestUri?.AbsoluteUri ==
                   "https://api.github.com/repos/cpp20120/CMakeWorkflow/actions/runs/99/artifacts?per_page=100&page=1" ||
                   request.RequestUri?.AbsoluteUri ==
                   "https://api.github.com/repos/cpp20120/CMakeWorkflow/actions/runs/99/artifacts?per_page=100&page=2"),
                "Catalog fetched an unpinned or unexpected URL.");
            return Task.FromResult(reply(request));
        }
    }
}
