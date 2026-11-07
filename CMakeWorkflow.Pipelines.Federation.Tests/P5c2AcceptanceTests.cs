using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Pipelines;
using CMakeWorkflow.Pipelines.Artifacts;
using CMakeWorkflow.Pipelines.Federation;
using CMakeWorkflow.Pipelines.GitHub;
using CMakeWorkflow.Pipelines.Persistence;

internal static class P5c2AcceptanceTests
{
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "cmwf-p5c2-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            await ReconnectAndStageIsolationAsync(root);
            await ExpiredGitHubArtifactAsync(root);
            await VerifiedGitHubArchiveAsync(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("P5c2 federation: ledger restart, explicit reconnect, stage isolation, expiry and read-only integrity passed.");
    }

    private static async Task ReconnectAndStageIsolationAsync(string root)
    {
        string database = Path.Combine(root, "reconnect.sqlite");
        string operationId;
        long expectedRevision;
        int expectedEvents;
        using (PipelineLedger initial = new(database))
        {
            PipelineRunSnapshot accepted = initial.Accept("p5c2-host", new PipelineDocument
            {
                SchemaVersion = 1,
                Name = "reconnect",
                Stages =
                [
                    new PipelineStage
                    {
                        Id = "compile", Kind = "workflow",
                        Workflow = new PipelineWorkflowStage { Manifest = "samples/compile.json" }
                    },
                    new PipelineStage
                    {
                        Id = "package", Kind = "workflow",
                        Workflow = new PipelineWorkflowStage { Manifest = "samples/package.json" }
                    }
                ]
            });
            PipelineRunSnapshot current = accepted;
            foreach (string stage in new[] { "compile", "package" })
            {
                current = initial.ReserveWorkflow(current.OperationId, stage, current.Revision);
                current = initial.BindChild(current.OperationId, stage, current.Revision, "child-" + stage);
                current = initial.RecordOutcome(current.OperationId, stage, current.Revision, true,
                    "report:sha256:" + new string('F', 64));
            }
            operationId = current.OperationId;
            expectedRevision = current.Revision;
            expectedEvents = initial.Events(operationId).Count;
        }

        // A fresh ledger and a fresh Host handle model an explicit reconnect without redispatch.
        using PipelineLedger restarted = new(database);
        FederatedArtifactCoordinator coordinator = new(restarted);
        byte[] content = Encoding.UTF8.GetBytes("verified after reconnect");
        FakeHost compile = new(ArtifactOrigin.HostSsh, "host-A/workspace-1", "child-compile", content);
        FakeHost package = new(ArtifactOrigin.HostSsh, "host-A/workspace-1", "child-package", content);
        FederatedArtifact compileArtifact = (await coordinator.ListAsync(operationId, "compile", compile)).Single();
        FederatedArtifact packageArtifact = (await coordinator.ListAsync(operationId, "package", package)).Single();
        Check(compileArtifact.Key != packageArtifact.Key &&
            compileArtifact.Key.StageId == "compile" && packageArtifact.Key.StageId == "package",
            "Two stages with the same artifact name/digest must have different keys.");

        await RefusedAsync<InvalidOperationException>(async () =>
            { _ = await coordinator.InspectAsync(compileArtifact.Key, package); });
        FakeHost wrongIdentity = new(ArtifactOrigin.HostSsh, "host-B/workspace-1", "child-compile", content);
        await RefusedAsync<KeyNotFoundException>(async () =>
            { _ = await coordinator.InspectAsync(compileArtifact.Key, wrongIdentity); });
        Check(package.Downloads == 0 && wrongIdentity.Downloads == 0,
            "Cross-stage or cross-host lookup reached download.");

        FakeHost disconnected = new(ArtifactOrigin.HostSsh, "host-A/workspace-1", "child-compile", content)
        {
            FailNextDownload = true
        };
        string target = Path.Combine(root, "reconnected.bin");
        await RefusedAsync<IOException>(async () =>
            { _ = await coordinator.FetchAsync(compileArtifact, target, disconnected); });
        Check(!File.Exists(target) && disconnected.Downloads == 1,
            "Interrupted transfer published a final destination.");

        // No implicit retry. A new caller-owned Host handle explicitly resumes the observation/fetch.
        FakeHost reconnected = new(ArtifactOrigin.HostSsh, "host-A/workspace-1", "child-compile", content);
        FederatedArtifactFetchReceipt receipt = await coordinator.FetchAsync(compileArtifact, target, reconnected);
        Check(reconnected.Downloads == 1 && await FederatedArtifactCoordinator.VerifyAsync(receipt),
            "Explicit reconnect failed verified retrieval.");
        await File.WriteAllTextAsync(target, "tampered");
        Check(!await FederatedArtifactCoordinator.VerifyAsync(receipt),
            "Offline verification accepted changed bytes.");
        PipelineRunSnapshot unchanged = restarted.Get(operationId);
        Check(unchanged.Revision == expectedRevision && restarted.Events(operationId).Count == expectedEvents,
            "Artifact transport mutated the durable pipeline ledger.");

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        int lists = reconnected.Lists;
        await RefusedAsync<OperationCanceledException>(async () =>
            { _ = await coordinator.ListAsync(operationId, "compile", reconnected, cancelled.Token); });
        Check(reconnected.Lists == lists, "Cancellation caused provider I/O.");
    }

    private static async Task ExpiredGitHubArtifactAsync(string root)
    {
        using PipelineLedger ledger = new(Path.Combine(root, "github.sqlite"));
        PipelineRunSnapshot accepted = ledger.Accept("p5c2-github", new PipelineDocument
        {
            SchemaVersion = 1,
            Name = "github-expiry",
            Stages = [new PipelineStage
            {
                Id = "ci", Kind = "github.actions",
                GitHubActions = new PipelineGitHubActionsStage
                {
                    Mode = "attach", Repository = "cpp20120/CMakeWorkflow", Workflow = "ci.yml",
                    RunId = 99, RunAttempt = 2, ExpectedHeadSha = CommitSha
                }
            }]
        });
        AttemptReader reader = new();
        GitHubActionsStageWatcher watcher = new(ledger, reader);
        _ = await watcher.AttachReadyAsync(accepted.OperationId, "ci");
        PipelineRunSnapshot terminal = await watcher.ObserveAsync(accepted.OperationId, "ci");
        long revision = terminal.Revision;
        int events = ledger.Events(terminal.OperationId).Count;
        using CatalogHandler handler = new();
        using HttpClient client = new(handler, disposeHandler: false);
        GitHubActionsArtifactCatalog catalog = new(client, reader);
        FederatedArtifactCoordinator coordinator = new(ledger, catalog);
        FederatedArtifact artifact = (await coordinator.ListAsync(terminal.OperationId, "ci")).Single();
        Check(artifact.ExecutionBinding == ArtifactExecutionBinding.RunOnly && !artifact.Expired,
            "GitHub run artifact falsely claimed attempt provenance or expiry.");
        handler.Expired = true; // Provider expiry changed since initial discovery.
        FederatedArtifact nowExpired = await coordinator.InspectAsync(artifact.Key);
        Check(nowExpired.Expired, "Expiry was not observed on reinspection.");
        string destination = Path.Combine(root, "expired-github");
        await RefusedAsync<InvalidDataException>(async () =>
            { _ = await coordinator.FetchAsync(artifact, destination); });
        Check(!Directory.Exists(destination) && handler.Requests == 3,
            "Expired artifact was published or bypassed the catalog recheck.");
        Check(ledger.Get(terminal.OperationId).Revision == revision &&
              ledger.Events(terminal.OperationId).Count == events,
            "GitHub expiry inspection mutated the durable ledger.");
    }

    private static async Task VerifiedGitHubArchiveAsync(string root)
    {
        using PipelineLedger ledger = new(Path.Combine(root, "github-archive.sqlite"));
        PipelineRunSnapshot accepted = ledger.Accept("p5c2-github-archive", new PipelineDocument
        {
            SchemaVersion = 1,
            Name = "github-archive",
            Stages = [new PipelineStage
            {
                Id = "ci", Kind = "github.actions",
                GitHubActions = new PipelineGitHubActionsStage
                {
                    Mode = "attach", Repository = "cpp20120/CMakeWorkflow", Workflow = "ci.yml",
                    RunId = 99, RunAttempt = 2, ExpectedHeadSha = CommitSha
                }
            }]
        });
        AttemptReader reader = new();
        GitHubActionsStageWatcher watcher = new(ledger, reader);
        _ = await watcher.AttachReadyAsync(accepted.OperationId, "ci");
        PipelineRunSnapshot terminal = await watcher.ObserveAsync(accepted.OperationId, "ci");
        long revision = terminal.Revision;
        byte[] zipped = BuildArchive();
        using CatalogHandler handler = new()
        {
            ArchiveSize = zipped.Length,
            ArchiveSha256 = Convert.ToHexString(SHA256.HashData(zipped))
        };
        using HttpClient client = new(handler, disposeHandler: false);
        GitHubActionsArtifactCatalog catalog = new(client, reader);
        ArchiveTransport transport = new(zipped);
        GitHubActionsArtifactDownloader downloader = new(catalog, transport, "TOKEN");
        FederatedArtifactCoordinator coordinator = new(ledger, catalog, downloader);
        FederatedArtifact artifact = (await coordinator.ListAsync(terminal.OperationId, "ci")).Single();
        string destination = Path.Combine(root, "github-verified");
        FederatedArtifactFetchReceipt receipt = await coordinator.FetchAsync(artifact, destination);
        Check(receipt.IsDirectory && receipt.Key == artifact.Key &&
              receipt.Sha256 == handler.ArchiveSha256 &&
              artifact.ExecutionBinding == ArtifactExecutionBinding.RunOnly &&
              transport.ApiRequests == 1 && transport.StorageRequests == 1 &&
              await FederatedArtifactCoordinator.VerifyAsync(receipt),
            "GitHub run-only ZIP was not fetched and verified through the federation facade.");
        await File.WriteAllTextAsync(Path.Combine(destination, "unexpected.txt"), "unexpected");
        Check(!await FederatedArtifactCoordinator.VerifyAsync(receipt),
            "Offline verification ignored a new file in the published GitHub directory.");
        Check(ledger.Get(terminal.OperationId).Revision == revision,
            "Successful GitHub artifact fetch changed pipeline execution state.");
    }

    private static byte[] BuildArchive()
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("logs/result.txt");
            using Stream stream = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes("verified GitHub artifact");
            stream.Write(bytes);
        }
        return buffer.ToArray();
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition)
        {
            throw new UnreachableException(reason);
        }
    }

    private static async Task RefusedAsync<T>(Func<Task> work) where T : Exception
    {
        try
        {
            await work();
        }
        catch (T)
        {
            return;
        }
        throw new UnreachableException("Expected " + typeof(T).Name);
    }

    private sealed class FakeHost(ArtifactOrigin origin, string identity, string operationId, byte[] bytes)
        : IWorkflowHostArtifactSource
    {
        public ArtifactOrigin Origin { get; } = origin;
        public string HostIdentity { get; } = identity;
        public string OperationId { get; } = operationId;
        public int Lists { get; private set; }
        public int Downloads { get; private set; }
        public bool FailNextDownload { get; set; }

        public Task<OperationArtifactList> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Lists++;
            return Task.FromResult(new OperationArtifactList(OperationId, new string('A', 64),
            [new OperationArtifactInfo("artifact", "log", null, "results/shared.log", bytes.Length,
                638900000000000000, Convert.ToHexString(SHA256.HashData(bytes)))]));
        }

        public async Task<string> DownloadAsync(OperationArtifactInfo artifact, string destinationPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Downloads++;
            _ = artifact;
            if (FailNextDownload)
            {
                FailNextDownload = false;
                throw new IOException("Simulated disconnected transport.");
            }
            await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
            return Path.GetFullPath(destinationPath);
        }
    }

    private sealed class AttemptReader : IGitHubActionsRunReader
    {
        public Task<GitHubRunAttempt> GetAttemptAsync(PipelineGitHubActionsStage expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GitHubRunAttempt(99, 2, "cpp20120/CMakeWorkflow",
                ".github/workflows/ci.yml", CommitSha, "completed", "success", new string('B', 64)));
        }
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public bool Expired { get; set; }
        public int Requests { get; private set; }
        public long ArchiveSize { get; set; } = 128;
        public string ArchiveSha256 { get; set; } = new string('A', 64);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(request.RequestUri?.AbsoluteUri ==
                "https://api.github.com/repos/cpp20120/CMakeWorkflow/actions/runs/99/artifacts?per_page=100&page=1",
                "Artifact catalog escaped the pinned GitHub run.");
            Requests++;
            string json = JsonSerializer.Serialize(new
            {
                total_count = 1,
                artifacts = new[]
                {
                    new
                    {
                        id = 42,
                        name = "example",
                        size_in_bytes = ArchiveSize,
                        expired = Expired,
                        expires_at = "2035-01-01T00:00:00Z",
                        digest = "sha256:" + ArchiveSha256,
                        workflow_run = new { id = 99, head_sha = CommitSha }
                    }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ArchiveTransport(byte[] bytes) : IGitHubArtifactHttpTransport
    {
        public int ApiRequests { get; private set; }
        public int StorageRequests { get; private set; }

        public Task<HttpResponseMessage> GetApiAsync(Uri uri, string? token,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(string.Equals(uri.AbsoluteUri,
                "https://api.github.com/repos/cpp20120/CMakeWorkflow/actions/artifacts/42/zip",
                StringComparison.OrdinalIgnoreCase) &&
                token == "TOKEN", "Artifact fetch escaped pinned API identity.");
            ApiRequests++;
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(
                "https://productionresultssa0.blob.core.windows.net/container/blob?sig=test");
            return Task.FromResult(response);
        }

        public Task<HttpResponseMessage> GetStorageAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(uri.Host == "productionresultssa0.blob.core.windows.net",
                "Artifact fetch escaped pinned storage origin.");
            StorageRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }
    }
}
