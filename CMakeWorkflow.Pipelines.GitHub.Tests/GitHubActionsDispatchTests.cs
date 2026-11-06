using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Pipelines;
using CMakeWorkflow.Pipelines.GitHub;
using CMakeWorkflow.Pipelines.Persistence;

internal static class GitHubActionsDispatchTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string BodyHash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "cmwf-p4-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(root);
        try
        {
            await SuccessRestartAndFinalizationAsync(Path.Combine(root, "success.sqlite"));
            await LostReplyNoRedispatchAsync(Path.Combine(root, "lost.sqlite"));
            await BeforeDispatchCrashAsync(Path.Combine(root, "before.sqlite"));
            await IdentityAndDependencyAsync(Path.Combine(root, "identity.sqlite"));
            await CompetingRevisionAsync(Path.Combine(root, "cas.sqlite"));
            await HttpDispatchContractAsync();
            RejectInvalidDispatchManifests();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("P4 GitHub dispatch: one-shot intent, receipt, correlation, restart, no redispatch and HTTP passed.");
    }

    private static PipelineDocument Document(bool dependent = false) => new()
    {
        SchemaVersion = 1,
        Name = "pipeline",
        Stages = dependent ? new[] { Local(), Remote(dependent: true) } : [Remote()]
    };

    private static PipelineStage Local() => new()
    {
        Id = "build",
        Kind = "workflow",
        Workflow = new PipelineWorkflowStage { Manifest = "workflow.json" }
    };

    private static PipelineStage Remote(bool dependent = false) => new()
    {
        Id = "ci",
        Kind = "github.actions",
        DependsOn = dependent ? ["build"] : [],
        GitHubActions = new PipelineGitHubActionsStage
        {
            Mode = "dispatch",
            Repository = "cpp20120/CMakeWorkflow",
            Workflow = "ci.yml",
            ExpectedHeadSha = Sha,
            Ref = "refs/heads/feature/pipelines-v1",
            Inputs = new Dictionary<string, string> { ["target"] = "linux" }
        }
    };

    private static GitHubRunAttempt Attempt(long id, string correlation, string status = "queued",
        string? conclusion = null) =>
        new(id, 1, "cpp20120/CMakeWorkflow", ".github/workflows/ci.yml", Sha, status, conclusion,
            BodyHash, "workflow_dispatch", "cmwf-" + correlation);

    private static void Check(bool predicate, string message)
    {
        if (!predicate)
        {
            throw new UnreachableException(message);
        }
    }

    private static async Task RefusedAsync<T>(Func<Task> call) where T : Exception
    {
        try { await call(); }
        catch (T) { return; }
        throw new UnreachableException("Expected " + typeof(T).Name);
    }

    private static async Task SuccessRestartAndFinalizationAsync(string file)
    {
        string op;
        string request;
        FakeDispatcher post = new();
        FakeReader remote = new();
        using (PipelineLedger ledger = new(file))
        {
            op = ledger.Accept("dispatch-success", Document()).OperationId;
            GitHubActionsDispatchCoordinator coordinator = new(ledger, post, remote);
            PipelineRunSnapshot bound = await coordinator.DispatchReadyAsync(op, "ci");
            PipelineStageSnapshot stage = bound.Stages.Single();
            Check(post.Posts == 1 && bound.Revision == 2 && stage.Status == PipelineStageStates.WaitingExternal,
                "One dispatch must create intent and bound receipt.");
            request = stage.ChildRequestId!;
            Check(post.LastCorrelation == request && stage.ExternalReference!.Contains(":12345:1:", StringComparison.Ordinal),
                "Receipt or correlation not pinned.");
            Check(ledger.Events(op).Select(e => e.Kind).SequenceEqual([
                "accepted", "github_dispatch_reserved", "github_dispatch_bound"]),
                "Expected durable write-ahead journal.");
            await RefusedAsync<InvalidOperationException>(async () =>
                { _ = await coordinator.DispatchReadyAsync(op, "ci"); });
            Check(post.Posts == 1, "Repeat request issued a second dispatch.");
        }

        using PipelineLedger restarted = new(file);
        GitHubActionsDispatchCoordinator observer = new(restarted, post, remote);
        remote.Result = Attempt(12345, request, "in_progress");
        PipelineRunSnapshot ongoing = await observer.ObserveAsync(op, "ci");
        Check(ongoing.Revision == 2 && post.Posts == 1 && remote.Reads == 1,
            "Restart must observe exact pinned run without POST.");
        remote.Result = Attempt(12345, request, "completed", "success");
        PipelineRunSnapshot done = await observer.ObserveAsync(op, "ci");
        Check(done.Stages.Single().Status == PipelineStageStates.Succeeded && done.Revision == 3 &&
              done.Stages.Single().EvidenceReference!.Contains("sha256:", StringComparison.Ordinal),
            "Verified terminal result was not journaled.");
        int reads = remote.Reads;
        Check((await observer.ObserveAsync(op, "ci")).Revision == 3 && remote.Reads == reads,
            "Terminal stage was reinterpreted.");
    }

    private static async Task LostReplyNoRedispatchAsync(string file)
    {
        FakeDispatcher post = new() { LoseReply = true };
        FakeReader remote = new();
        string op;
        string correlation;
        using (PipelineLedger ledger = new(file))
        {
            op = ledger.Accept("dispatch-lost", Document()).OperationId;
            GitHubActionsDispatchCoordinator coordinator = new(ledger, post, remote);
            await RefusedAsync<IOException>(async () => { _ = await coordinator.DispatchReadyAsync(op, "ci"); });
            PipelineRunSnapshot unresolved = ledger.Get(op);
            correlation = unresolved.Stages.Single().ChildRequestId!;
            Check(unresolved.Stages.Single().Status == PipelineStageStates.DispatchIntent && post.Posts == 1,
                "Lost response erased durable intent.");
        }

        using PipelineLedger reopened = new(file);
        GitHubActionsDispatchCoordinator recovered = new(reopened, post, remote);
        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await recovered.DispatchReadyAsync(op, "ci"); });
        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await recovered.ObserveAsync(op, "ci"); });
        Check(post.Posts == 1, "Lost response triggered a second POST.");
        remote.Result = Attempt(12345, correlation) with { DisplayTitle = "wrong" };
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await recovered.ReconcileKnownRunAsync(op, "ci", 12345); });
        Check(reopened.Get(op).Revision == 1, "Uncorrelated candidate was incorrectly accepted.");
        remote.Result = Attempt(12345, correlation);
        PipelineRunSnapshot resolved = await recovered.ReconcileKnownRunAsync(op, "ci", 12345);
        Check(resolved.Revision == 2 && resolved.Stages.Single().Status == PipelineStageStates.WaitingExternal &&
              post.Posts == 1, "Read-only reconciliation did not pin original run.");
        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await recovered.ReconcileKnownRunAsync(op, "ci", 12345); });
    }

    private static async Task BeforeDispatchCrashAsync(string file)
    {
        string op;
        using (PipelineLedger ledger = new(file))
        {
            PipelineRunSnapshot accepted = ledger.Accept("before-post", Document());
            op = accepted.OperationId;
            _ = ledger.ReserveGitHubDispatch(op, "ci", accepted.Revision);
        }
        FakeDispatcher post = new();
        FakeReader remote = new();
        using PipelineLedger reopened = new(file);
        GitHubActionsDispatchCoordinator coordinator = new(reopened, post, remote);
        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await coordinator.DispatchReadyAsync(op, "ci"); });
        Check(post.Posts == 0 && reopened.Get(op).Revision == 1,
            "Crash before POST cannot authorize another dispatch.");
    }

    private static async Task IdentityAndDependencyAsync(string file)
    {
        FakeDispatcher post = new();
        FakeReader remote = new();
        using PipelineLedger ledger = new(file);
        string op = ledger.Accept("blocked", Document(dependent: true)).OperationId;
        GitHubActionsDispatchCoordinator coordinator = new(ledger, post, remote);
        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await coordinator.DispatchReadyAsync(op, "ci"); });
        try
        {
            _ = ledger.WatchExistingGitHub(op, "ci", 0);
            throw new UnreachableException("Legacy attach method accepted dispatch mode.");
        }
        catch (InvalidOperationException)
        {
        }
        Check(post.Posts == 0 && ledger.Get(op).Revision == 0, "Blocked dependency caused POST.");
        PipelineRunSnapshot allowed = ledger.ReserveWorkflow(op, "build", 0);
        _ = ledger.BindChild(op, "build", allowed.Revision, "child-id");
        _ = ledger.RecordOutcome(op, "build", allowed.Revision + 1, true, "child-report:verified");
        PipelineRunSnapshot bound = await coordinator.DispatchReadyAsync(op, "ci");
        string correlation = bound.Stages.Single(s => s.Id == "ci").ChildRequestId!;
        remote.Result = Attempt(12345, correlation) with { HeadSha = new string('F', 40) };
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await coordinator.ObserveAsync(op, "ci"); });
        remote.Result = Attempt(12345, correlation) with { Event = "push" };
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await coordinator.ObserveAsync(op, "ci"); });
        Check(ledger.Get(op).Stages.Single(s => s.Id == "ci").Status == PipelineStageStates.WaitingExternal,
            "Mismatched provider identity changed durable stage.");
        remote.Result = Attempt(12345, correlation, "completed", "cancelled");
        Check((await coordinator.ObserveAsync(op, "ci")).Stages.Single(s => s.Id == "ci").Status ==
              PipelineStageStates.Failed, "Cancelled dispatch must not succeed.");
    }

    private static async Task CompetingRevisionAsync(string file)
    {
        FakeDispatcher post = new();
        FakeReader remote = new();
        using PipelineLedger primary = new(file);
        using PipelineLedger concurrent = new(file);
        PipelineDocument document = new()
        {
            SchemaVersion = 1,
            Name = "independent",
            Stages = [Remote(), Local()]
        };
        string op = primary.Accept("cas", document).OperationId;
        GitHubActionsDispatchCoordinator coordinator = new(primary, post, remote);
        post.BeforeReturn = () =>
        {
            PipelineRunSnapshot current = concurrent.Get(op);
            Check(current.Stages.Single(s => s.Id == "ci").Status == PipelineStageStates.DispatchIntent,
                "POST occurred before SQLite intent committed.");
            _ = concurrent.ReserveWorkflow(op, "build", current.Revision);
        };
        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await coordinator.DispatchReadyAsync(op, "ci"); });
        PipelineRunSnapshot after = primary.Get(op);
        string correlation = after.Stages.Single(s => s.Id == "ci").ChildRequestId!;
        Check(post.Posts == 1 && after.Revision == 2 &&
              after.Stages.Single(s => s.Id == "ci").Status == PipelineStageStates.DispatchIntent,
            "Stale provider receipt bypassed CAS or redispatched.");
        remote.Result = Attempt(12345, correlation);
        PipelineRunSnapshot bound = await coordinator.ReconcileKnownRunAsync(op, "ci", 12345);
        Check(bound.Revision == 3 && post.Posts == 1,
            "CAS conflict not recoverable through verified read-only reconciliation.");
    }

    private static async Task HttpDispatchContractAsync()
    {
        using DispatchHandler handler = new();
        using HttpClient client = new(handler, disposeHandler: false);
        GitHubActionsHttpDispatcher dispatcher = new(client, "test-write-token");
        PipelineGitHubActionsStage options = Remote().GitHubActions!;
        GitHubDispatchReceipt receipt = await dispatcher.DispatchOnceAsync(options, "pipeline:corr");
        Check(receipt.RunId == 12345 && handler.Posts == 1 && handler.Method == HttpMethod.Post &&
              handler.Address == "https://api.github.com/repos/cpp20120/CMakeWorkflow/actions/workflows/ci.yml/dispatches" &&
              handler.Authorization?.Scheme == "Bearer" && handler.Authorization.Parameter == "test-write-token" &&
              handler.Accept == "application/vnd.github+json", "Dispatch HTTP contract mismatch.");
        using (JsonDocument doc = JsonDocument.Parse(handler.Body!))
        {
            JsonElement root = doc.RootElement;
            Check(root.GetProperty("return_run_details").GetBoolean() &&
                  root.GetProperty("ref").GetString() == "refs/heads/feature/pipelines-v1" &&
                  root.GetProperty("inputs").GetProperty("cmwf_request_id").GetString() == "pipeline:corr" &&
                  root.GetProperty("inputs").GetProperty("target").GetString() == "linux", "POST JSON mismatch.");
        }
        handler.Status = HttpStatusCode.NoContent;
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await dispatcher.DispatchOnceAsync(options, "corr"); });
        handler.Status = HttpStatusCode.OK;
        handler.ResponseBody = /*lang=json,strict*/ "{\"workflow_run_id\":12345,\"run_url\":\"https://evil.example/foo\"}";
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await dispatcher.DispatchOnceAsync(options, "corr"); });
        handler.ResponseBody = "{}";
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await dispatcher.DispatchOnceAsync(options, "corr"); });
        handler.Status = HttpStatusCode.TooManyRequests;
        await RefusedAsync<HttpRequestException>(async () =>
            { _ = await dispatcher.DispatchOnceAsync(options, "corr"); });
        Check(handler.Posts == 5, "Dispatcher retried an ambiguous response.");

        // Real HTTP parsing must surface the correlation fields, not just fake reader data.
        handler.Status = HttpStatusCode.OK;
        handler.ResponseBody = "{\"id\":12345,\"run_attempt\":1,\"repository\":{\"full_name\":\"cpp20120/CMakeWorkflow\"}," +
            "\"path\":\".github/workflows/ci.yml@refs/heads/main\",\"head_sha\":\"" + Sha +
            "\",\"status\":\"queued\",\"conclusion\":null,\"event\":\"workflow_dispatch\"," +
            "\"display_title\":\"cmwf-pipeline:corr\"}";
        GitHubActionsHttpReader reader = new(client, "read-token");
        GitHubRunAttempt observed = await reader.GetAttemptAsync(new PipelineGitHubActionsStage
        {
            Mode = "attach",
            Repository = options.Repository,
            Workflow = options.Workflow,
            RunId = 12345,
            RunAttempt = 1,
            ExpectedHeadSha = Sha
        });
        Check(observed.Event == "workflow_dispatch" && observed.DisplayTitle == "cmwf-pipeline:corr" &&
              observed.RunId == 12345, "HTTP attempt reader lost dispatch correlation metadata.");
    }

    private static void RejectInvalidDispatchManifests()
    {
        PipelineGitHubActionsStage valid = Remote().GitHubActions!;
        foreach (PipelineGitHubActionsStage invalid in new[]
        {
            new PipelineGitHubActionsStage { Mode = "dispatch", Repository = valid.Repository,
                Workflow = valid.Workflow, ExpectedHeadSha = valid.ExpectedHeadSha },
            new PipelineGitHubActionsStage { Mode = "dispatch", Repository = valid.Repository,
                Workflow = valid.Workflow, ExpectedHeadSha = valid.ExpectedHeadSha, Ref = valid.Ref,
                RunId = 7 },
            new PipelineGitHubActionsStage { Mode = "dispatch", Repository = valid.Repository,
                Workflow = valid.Workflow, ExpectedHeadSha = valid.ExpectedHeadSha, Ref = valid.Ref,
                Inputs = new Dictionary<string, string> { ["cmwf_request_id"] = "spoof" } },
            new PipelineGitHubActionsStage { Mode = "dispatch", Repository = valid.Repository,
                Workflow = "evil/path/ci.yml", ExpectedHeadSha = valid.ExpectedHeadSha, Ref = valid.Ref },
            new PipelineGitHubActionsStage { Mode = "dispatch", Repository = valid.Repository,
                Workflow = valid.Workflow, ExpectedHeadSha = "bad", Ref = valid.Ref }
        })
        {
            try
            {
                _ = PipelinePlanner.Plan(new PipelineDocument
                {
                    SchemaVersion = 1,
                    Name = "invalid",
                    Stages = [new PipelineStage { Id = "ci", Kind = "github.actions", GitHubActions = invalid }]
                });
            }
            catch (ArgumentException)
            {
                continue;
            }
            throw new UnreachableException("Invalid GitHub dispatch manifest accepted.");
        }
    }

    private sealed class FakeDispatcher : IGitHubActionsDispatcher
    {
        public int Posts { get; private set; }
        public string? LastCorrelation { get; private set; }
        public bool LoseReply { get; init; }
        public Action? BeforeReturn { get; set; }

        public Task<GitHubDispatchReceipt> DispatchOnceAsync(PipelineGitHubActionsStage stage, string correlationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Posts++;
            LastCorrelation = correlationId;
            BeforeReturn?.Invoke();
            return LoseReply ? throw new IOException("Response lost after GitHub accepted dispatch.") :
                Task.FromResult(new GitHubDispatchReceipt(12345));
        }
    }

    private sealed class FakeReader : IGitHubActionsRunReader
    {
        public int Reads { get; private set; }
        public GitHubRunAttempt Result { get; set; } = Attempt(12345, "unused");
        public Task<GitHubRunAttempt> GetAttemptAsync(PipelineGitHubActionsStage expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Result);
        }
    }

    private sealed class DispatchHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } =
            "{\"workflow_run_id\":12345,\"run_url\":\"https://api.github.com/repos/cpp20120/CMakeWorkflow/actions/runs/12345\"," +
            "\"html_url\":\"https://github.com/cpp20120/CMakeWorkflow/actions/runs/12345\"}";
        public string? Address { get; private set; }
        public string? Body { get; private set; }
        public HttpMethod? Method { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Accept { get; private set; }
        public int Posts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Posts++;
            Address = request.RequestUri?.ToString();
            Method = request.Method;
            Authorization = request.Headers.Authorization;
            Accept = request.Headers.Accept.FirstOrDefault()?.MediaType;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent(ResponseBody, Encoding.UTF8) };
        }
    }
}
