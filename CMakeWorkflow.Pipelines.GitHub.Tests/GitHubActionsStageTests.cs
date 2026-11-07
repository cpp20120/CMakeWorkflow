using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CMakeWorkflow.Pipelines;
using CMakeWorkflow.Pipelines.GitHub;
using CMakeWorkflow.Pipelines.Persistence;

internal static class GitHubActionsStageTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string BodyHash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "cmwf-p3-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(root);
        try
        {
            await SuccessAndRestartAsync(Path.Combine(root, "success.sqlite"));
            await DependencyAndFailureAsync(Path.Combine(root, "failure.sqlite"));
            await RefuseIdentityMismatchesAsync(Path.Combine(root, "mismatch.sqlite"));
            await TransientAndStaleAsync(Path.Combine(root, "transient.sqlite"));
            await HttpReaderAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("P3 GitHub attach/watch: pinned attempt, restart, verified outcome, API errors and CAS passed.");
    }

    private static PipelineDocument Document(bool dependent = false) => new()
    {
        SchemaVersion = 1,
        Name = "pipeline",
        Stages = dependent
            ? new[] { GitHub("ci"), GitHub("release", ["ci"]) }
            : [GitHub("ci")]
    };

    private static PipelineStage GitHub(string id, IReadOnlyList<string>? dependsOn = null) => new()
    {
        Id = id,
        Kind = "github.actions",
        DependsOn = dependsOn ?? [],
        GitHubActions = Options()
    };

    private static PipelineGitHubActionsStage Options() => new()
    {
        Mode = "attach",
        Repository = "cpp20120/CMakeWorkflow",
        Workflow = "ci.yml",
        RunId = 42297281115,
        RunAttempt = 1,
        ExpectedHeadSha = Sha
    };

    private static GitHubRunAttempt Attempt(string status = "queued", string? conclusion = null) =>
        new(42297281115, 1, "cpp20120/CMakeWorkflow", ".github/workflows/ci.yml",
            Sha, status, conclusion, BodyHash);

    private static void Check(bool condition, string reason)
    {
        if (!condition)
        {
            throw new UnreachableException(reason);
        }
    }

    private static async Task RefusedAsync<T>(Func<Task> operation) where T : Exception
    {
        try
        {
            await operation();
        }
        catch (T)
        {
            return;
        }

        throw new UnreachableException("Expected failure: " + typeof(T).Name);
    }

    private static async Task SuccessAndRestartAsync(string path)
    {
        string operation;
        FakeReader remote = new() { Result = Attempt() };
        using (PipelineLedger ledger = new(path))
        {
            PipelineRunSnapshot accepted = ledger.Accept("github-success", Document());
            operation = accepted.OperationId;
            GitHubActionsStageWatcher coordinator = new(ledger, remote);
            PipelineRunSnapshot attached = await coordinator.AttachReadyAsync(operation, "ci");
            Check(attached.Stages[0].Status == PipelineStageStates.WaitingExternal && attached.Revision == 1,
                "Attach did not record a durable external reference.");
            Check(attached.Stages[0].ExternalReference ==
                "github:cpp20120/CMakeWorkflow:ci.yml:42297281115:1:" + Sha,
                "Incorrect persisted GitHub binding.");
            Check((await coordinator.ObserveAsync(operation, "ci")).Revision == 1,
                "Nonterminal attempt was finalized.");
        }

        remote.Result = Attempt("completed", "success");
        using PipelineLedger restarted = new(path);
        GitHubActionsStageWatcher watcher = new(restarted, remote);
        PipelineRunSnapshot finished = await watcher.ObserveAsync(operation, "ci");
        Check(finished.Status == "succeeded" && finished.Stages[0].Status == PipelineStageStates.Succeeded,
            "Verified completed success was not persisted after restart.");
        Check(finished.Stages[0].EvidenceReference!.Contains(BodyHash, StringComparison.Ordinal),
            "Response hash not retained in terminal evidence.");
        int reads = remote.Reads;
        Check((await watcher.WatchUntilTerminalAsync(operation, "ci")).Revision == finished.Revision &&
              remote.Reads == reads, "Finalized stage was reinterpreted after rerun.");
        await RefusedAsync<ArgumentOutOfRangeException>(async () =>
            { _ = await watcher.WatchUntilTerminalAsync(operation, "ci", TimeSpan.FromMilliseconds(50)); });
        Check(restarted.Events(operation).Select(e => e.Revision).SequenceEqual([0L, 1L, 2L]),
            "GitHub journal revisions are not contiguous.");
    }

    private static async Task DependencyAndFailureAsync(string path)
    {
        using PipelineLedger ledger = new(path);
        FakeReader remote = new() { Result = Attempt() };
        GitHubActionsStageWatcher watcher = new(ledger, remote);
        PipelineRunSnapshot accepted = ledger.Accept("github-dependency", Document(dependent: true));
        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await watcher.AttachReadyAsync(accepted.OperationId, "release"); });
        Check(remote.Reads == 0, "Blocked stage caused an unnecessary provider request.");
        _ = await watcher.AttachReadyAsync(accepted.OperationId, "ci");
        remote.Result = Attempt("completed", "cancelled");
        PipelineRunSnapshot outcome = await watcher.ObserveAsync(accepted.OperationId, "ci");
        Check(outcome.Status == "failed" && outcome.Stages[0].Status == PipelineStageStates.Failed,
            "Canceled GitHub attempt must fail the pipeline stage.");
        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await watcher.AttachReadyAsync(accepted.OperationId, "release"); });
        Check(remote.Reads == 2, "Failure did not block dependent stage.");
    }

    private static async Task RefuseIdentityMismatchesAsync(string path)
    {
        using PipelineLedger ledger = new(path);
        PipelineRunSnapshot accepted = ledger.Accept("github-identity", Document());
        FakeReader remote = new() { Result = Attempt() with { RunAttempt = 2 } };
        GitHubActionsStageWatcher watcher = new(ledger, remote);
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await watcher.AttachReadyAsync(accepted.OperationId, "ci"); });
        Check(ledger.Get(accepted.OperationId).Revision == 0, "Incorrect attempt admitted.");

        foreach (GitHubRunAttempt mismatch in new[]
        {
            Attempt() with { RunId = 99 },
            Attempt() with { Repository = "other/repo" },
            Attempt() with { WorkflowPath = ".github/workflows/evil.yml" },
            Attempt() with { HeadSha = new string('F', 40) },
            Attempt() with { ResponseSha256 = "invalid" },
            Attempt("completed", null),
            Attempt("completed", "unknown_conclusion"),
            Attempt("in_progress", "success")
        })
        {
            remote.Result = mismatch;
            await RefusedAsync<InvalidDataException>(async () =>
                { _ = await watcher.AttachReadyAsync(accepted.OperationId, "ci"); });
            Check(ledger.Get(accepted.OperationId).Revision == 0, "Invalid API state mutated ledger.");
        }

        remote.Result = Attempt();
        _ = await watcher.AttachReadyAsync(accepted.OperationId, "ci");
        remote.Result = Attempt("completed", "success") with { HeadSha = new string('F', 40) };
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await watcher.ObserveAsync(accepted.OperationId, "ci"); });
        Check(ledger.Get(accepted.OperationId).Stages[0].Status == PipelineStageStates.WaitingExternal,
            "Mismatched observation incorrectly finalized stage.");
    }

    private static async Task TransientAndStaleAsync(string path)
    {
        using PipelineLedger ledger = new(path);
        PipelineDocument pair = new()
        {
            SchemaVersion = 1,
            Name = "pipeline",
            Stages = [GitHub("ci"), GitHub("other")]
        };
        PipelineRunSnapshot accepted = ledger.Accept("github-transient", pair);
        FakeReader remote = new() { Result = Attempt(), Fail = true };
        GitHubActionsStageWatcher watcher = new(ledger, remote);
        await RefusedAsync<HttpRequestException>(async () =>
            { _ = await watcher.AttachReadyAsync(accepted.OperationId, "ci"); });
        Check(ledger.Get(accepted.OperationId).Revision == 0, "Network error changed pending stage.");
        remote.Fail = false;
        _ = await watcher.AttachReadyAsync(accepted.OperationId, "ci");
        remote.Fail = true;
        await RefusedAsync<HttpRequestException>(async () =>
            { _ = await watcher.ObserveAsync(accepted.OperationId, "ci"); });
        Check(ledger.Get(accepted.OperationId).Revision == 1, "Network error finalized attached stage.");
        remote.Fail = false;

        // A second connection mutates the pipeline after a stale reader starts: CAS must reject the old view.
        using PipelineLedger competing = new(path);
        remote.Result = Attempt("completed", "success");
        remote.BeforeReturn = () =>
        {
            PipelineRunSnapshot current = competing.Get(accepted.OperationId);
            _ = competing.WatchExistingGitHub(accepted.OperationId, "other", current.Revision);
            remote.BeforeReturn = null;
        };
        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await watcher.ObserveAsync(accepted.OperationId, "ci"); });
        Check(ledger.Get(accepted.OperationId).Stages[0].Status == PipelineStageStates.WaitingExternal &&
              ledger.Get(accepted.OperationId).Revision == 2, "Stale provider result bypassed ledger CAS.");
        _ = await watcher.ObserveAsync(accepted.OperationId, "ci");
        Check(ledger.Get(accepted.OperationId).Revision == 3, "Fresh verification did not finalize.");
    }

    private static async Task HttpReaderAsync()
    {
        PipelineGitHubActionsStage expected = Options();
        using RecordingHandler handler = new();
        using HttpClient http = new(handler, disposeHandler: false);
        GitHubActionsHttpReader adapter = new(http, "read-token");
        handler.ResponseStatus = HttpStatusCode.OK;
        handler.Body = Response("in_progress", null);
        GitHubRunAttempt snapshot = await adapter.GetAttemptAsync(expected);
        Check(snapshot.Status == "in_progress" && snapshot.Conclusion is null &&
              snapshot.WorkflowPath == ".github/workflows/ci.yml", "GitHub JSON parsing failed.");
        Check(handler.Method == HttpMethod.Get && handler.RequestAddress ==
              "https://api.github.com/repos/cpp20120/CMakeWorkflow/actions/runs/42297281115/attempts/1",
            "Adapter did not request the pinned attempt endpoint.");
        Check(handler.Authorization?.Scheme == "Bearer" && handler.Authorization.Parameter == "read-token" &&
              handler.Accept == "application/vnd.github+json", "Missing GitHub API headers.");

        handler.ResponseStatus = HttpStatusCode.TooManyRequests;
        await RefusedAsync<HttpRequestException>(async () =>
            { _ = await adapter.GetAttemptAsync(expected); });
        handler.ResponseStatus = HttpStatusCode.OK;
        handler.Body = new string('x', (1024 * 1024) + 1);
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await adapter.GetAttemptAsync(expected); });
        handler.Body = /*lang=json,strict*/ "{\"id\":1}";
        await RefusedAsync<KeyNotFoundException>(async () =>
            { _ = await adapter.GetAttemptAsync(expected); });
        Check(handler.Count == 4, "Unexpected extra provider requests.");
    }

    private static string Response(string status, string? conclusion) =>
        "{\"id\":42297281115,\"run_attempt\":1,\"repository\":{\"full_name\":\"cpp20120/CMakeWorkflow\"}," +
        "\"head_sha\":\"" + Sha + "\",\"path\":\".github/workflows/ci.yml@refs/heads/main\"," +
        "\"status\":\"" + status + "\",\"conclusion\":" +
        (conclusion is null ? "null" : "\"" + conclusion + "\"") + "}";

    private sealed class FakeReader : IGitHubActionsRunReader
    {
        public GitHubRunAttempt Result { get; set; } = Attempt();
        public bool Fail { get; set; }
        public int Reads { get; private set; }
        public Action? BeforeReturn { get; set; }

        public Task<GitHubRunAttempt> GetAttemptAsync(PipelineGitHubActionsStage expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (Fail)
            {
                throw new HttpRequestException("Rate limited / connection lost.");
            }

            BeforeReturn?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode ResponseStatus { get; set; }
        public string Body { get; set; } = "";
        public string? RequestAddress { get; private set; }
        public HttpMethod? Method { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Accept { get; private set; }
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            RequestAddress = request.RequestUri?.ToString();
            Method = request.Method;
            Authorization = request.Headers.Authorization;
            Accept = request.Headers.Accept.FirstOrDefault()?.MediaType;
            HttpResponseMessage response = new(ResponseStatus)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
