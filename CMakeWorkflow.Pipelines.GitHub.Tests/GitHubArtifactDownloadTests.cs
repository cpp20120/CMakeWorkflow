using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Pipelines;
using CMakeWorkflow.Pipelines.Artifacts;
using CMakeWorkflow.Pipelines.GitHub;
using CMakeWorkflow.Pipelines.Persistence;

internal static class GitHubArtifactDownloadTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string StorageUrl = "https://productionresultssa0.blob.core.windows.net/artifact/file?sig=test";

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "cmwf-p5b2-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            await SuccessAsync(root);
            await RefuseChangesAndUnsafeRedirectsAsync(root);
            await RefuseProviderFailuresAsync(root);
            await RefuseWrongBytesAsync(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("P5b2 GitHub download: pinned ID, one-hop redirect, storage isolation and verified publication passed.");
    }

    private static async Task SuccessAsync(string root)
    {
        byte[] zip = Zip();
        using CatalogFixture fixture = new(zip);
        FederatedArtifact artifact = await fixture.ArtifactAsync();
        FakeTransport transport = new() { Archive = zip };
        GitHubActionsArtifactDownloader downloader = new(fixture.Catalog, transport, "SECRET");
        string destination = Path.Combine(root, "success");
        GitHubArtifactExtractionReceipt receipt = await downloader.FetchAsync(
            fixture.Pipeline, "ci", artifact, destination);
        Check(receipt.Key == artifact.Key &&
            receipt.ArchiveSha256 == Convert.ToHexString(SHA256.HashData(zip)) &&
            receipt.Files.Count == 1 &&
            await File.ReadAllTextAsync(Path.Combine(destination, "output.txt")) == "hello" &&
            transport.ApiRequests == 1 && transport.StorageRequests == 1 &&
            transport.ObservedToken == "SECRET" &&
            transport.ObservedApi?.AbsoluteUri ==
                "https://api.github.com/repos/CPP20120/CMAKEWORKFLOW/actions/artifacts/12/zip" &&
            transport.ObservedStorage?.AbsoluteUri == StorageUrl,
            "Artifact bytes or pinned HTTP authority were not verified.");
        Check(receipt.Key.Origin == ArtifactOrigin.GitHubActions &&
            artifact.ExecutionBinding == ArtifactExecutionBinding.RunOnly,
            "Download invented attempt-specific provenance.");
    }

    private static async Task RefuseChangesAndUnsafeRedirectsAsync(string root)
    {
        byte[] zip = Zip();
        using CatalogFixture fixture = new(zip);
        FederatedArtifact artifact = await fixture.ArtifactAsync();
        FakeTransport unused = new() { Archive = zip };
        GitHubActionsArtifactDownloader downloader = new(fixture.Catalog, unused, "SECRET");
        await RefusedAsync<InvalidDataException>(() => downloader.FetchAsync(
            fixture.Pipeline, "ci", artifact with { Sha256 = new string('F', 64) },
            Path.Combine(root, "changed")));
        await RefusedAsync<InvalidDataException>(() => downloader.FetchAsync(
            fixture.Pipeline, "ci", artifact with { Key = artifact.Key with { ArtifactId = "13" } },
            Path.Combine(root, "wrong-id")));
        Check(unused.ApiRequests == 0 && unused.StorageRequests == 0,
            "Uncatalogued artifact reached the download transport.");

        foreach (string url in new[]
        {
            "http://productionresultssa0.blob.core.windows.net/data",
            "https://127.0.0.1/data",
            "https://localhost/data",
            "https://productionresultssa0.blob.core.windows.net.attacker.example/data",
            "https://user:pass@productionresultssa0.blob.core.windows.net/data",
            "https://productionresultssa0.blob.core.windows.net:8443/data",
            "https://productionresultssa0.blob.core.windows.net/data#fragment"
        })
        {
            FakeTransport transport = new() { Archive = zip, Redirect = new Uri(url) };
            GitHubActionsArtifactDownloader rejected = new(fixture.Catalog, transport, "SECRET");
            await RefusedAsync<InvalidDataException>(() => rejected.FetchAsync(
                fixture.Pipeline, "ci", artifact, Path.Combine(root, "bad-redirect")));
            Check(transport.ApiRequests == 1 && transport.StorageRequests == 0 &&
                !Directory.Exists(Path.Combine(root, "bad-redirect")),
                "Unsafe redirect was fetched or published.");
        }
    }

    private static async Task RefuseProviderFailuresAsync(string root)
    {
        byte[] zip = Zip();
        using CatalogFixture fixture = new(zip);
        FederatedArtifact artifact = await fixture.ArtifactAsync();
        foreach (HttpStatusCode apiStatus in new[]
        {
            HttpStatusCode.Gone, HttpStatusCode.OK, HttpStatusCode.Forbidden
        })
        {
            FakeTransport transport = new() { Archive = zip, ApiStatus = apiStatus };
            GitHubActionsArtifactDownloader rejected = new(fixture.Catalog, transport);
            Task Fetch() => rejected.FetchAsync(
                fixture.Pipeline, "ci", artifact, Path.Combine(root, "api-failed"));
            if (apiStatus == HttpStatusCode.Gone)
            {
                await RefusedAsync<InvalidOperationException>(Fetch);
            }
            else if (apiStatus == HttpStatusCode.Forbidden)
            {
                await RefusedAsync<HttpRequestException>(Fetch);
            }
            else
            {
                await RefusedAsync<InvalidDataException>(Fetch);
            }
            Check(transport.ApiRequests == 1 && transport.StorageRequests == 0,
                "Unexpected storage request on failed GitHub API hop.");
        }
        FakeTransport noLocation = new() { Archive = zip, OmitRedirect = true };
        await RefusedAsync<InvalidDataException>(() => new GitHubActionsArtifactDownloader(
            fixture.Catalog, noLocation).FetchAsync(fixture.Pipeline, "ci", artifact,
            Path.Combine(root, "missing-location")));
        Check(noLocation.StorageRequests == 0, "Missing Location reached blob storage.");

        FakeTransport storageRedirect = new() { Archive = zip, StorageStatus = HttpStatusCode.Redirect };
        await RefusedAsync<InvalidDataException>(() => new GitHubActionsArtifactDownloader(
            fixture.Catalog, storageRedirect).FetchAsync(fixture.Pipeline, "ci", artifact,
            Path.Combine(root, "storage-redirect")));
        Check(storageRedirect.StorageRequests == 1 && !Directory.Exists(Path.Combine(root, "storage-redirect")),
            "Storage redirect was followed or published.");
    }

    private static async Task RefuseWrongBytesAsync(string root)
    {
        byte[] zip = Zip();
        using CatalogFixture fixture = new(zip);
        FederatedArtifact artifact = await fixture.ArtifactAsync();
        byte[] modified = (byte[])zip.Clone();
        modified[0] ^= 0xFF;
        FakeTransport transport = new() { Archive = modified };
        await RefusedAsync<InvalidDataException>(() => new GitHubActionsArtifactDownloader(
            fixture.Catalog, transport).FetchAsync(fixture.Pipeline, "ci", artifact,
            Path.Combine(root, "wrong-hash")));
        Check(!Directory.Exists(Path.Combine(root, "wrong-hash")),
            "Unverified archive was published.");
    }

    private static byte[] Zip()
    {
        using MemoryStream buffer = new();
        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry("output.txt");
            using Stream target = entry.Open();
            byte[] data = Encoding.UTF8.GetBytes("hello");
            target.Write(data);
        }
        return buffer.ToArray();
    }

    private static PipelineRunSnapshot Pipeline() => new(
        "pipeline-1", "request-1", "test", new string('C', 64),
        PipelineManifest.ToJson(new PipelineDocument
        {
            SchemaVersion = 1,
            Name = "test",
            Stages = [new PipelineStage
            {
                Id = "ci", Kind = "github.actions",
                GitHubActions = new PipelineGitHubActionsStage
                {
                    Mode = "attach", Repository = "cpp20120/CMakeWorkflow", Workflow = "ci.yml",
                    RunId = 99, RunAttempt = 2, ExpectedHeadSha = Sha
                }
            }]
        }),
        2, PipelineStageStates.Succeeded,
        [new PipelineStageSnapshot("ci", "github.actions", PipelineStageStates.Succeeded, 2,
            null, null, "github:cpp20120/CMakeWorkflow:ci.yml:99:2:" + Sha,
            "github-attempt:cpp20120/CMakeWorkflow:99:2:sha256:" + new string('B', 64))]);

    private sealed class CatalogFixture : IDisposable
    {
        private readonly CatalogHandler handler;
        private readonly HttpClient http;
        internal PipelineRunSnapshot Pipeline { get; } = GitHubArtifactDownloadTests.Pipeline();
        internal GitHubActionsArtifactCatalog Catalog { get; }

        internal CatalogFixture(byte[] zip)
        {
            string digest = Convert.ToHexString(SHA256.HashData(zip));
            string body = System.Text.Json.JsonSerializer.Serialize(new
            {
                total_count = 1,
                artifacts = new[]
                {
                    new
                    {
                        id = 12,
                        name = "results",
                        size_in_bytes = zip.Length,
                        expired = false,
                        expires_at = "2035-01-01T00:00:00Z",
                        digest = "sha256:" + digest,
                        workflow_run = new { id = 99, head_sha = Sha }
                    }
                }
            });
            handler = new CatalogHandler(body);
            http = new HttpClient(handler, disposeHandler: false);
            Catalog = new GitHubActionsArtifactCatalog(http, new Reader());
        }

        internal async Task<FederatedArtifact> ArtifactAsync() =>
            (await Catalog.ListAsync(Pipeline, "ci")).Single();

        public void Dispose()
        {
            http.Dispose();
            handler.Dispose();
        }
    }

    private sealed class CatalogHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Check(request.RequestUri?.AbsoluteUri ==
                "https://api.github.com/repos/cpp20120/CMakeWorkflow/actions/runs/99/artifacts?per_page=100&page=1",
                "Catalog requested unexpected GitHub run.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Reader : IGitHubActionsRunReader
    {
        public Task<GitHubRunAttempt> GetAttemptAsync(PipelineGitHubActionsStage expected,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitHubRunAttempt(99, 2, "cpp20120/CMakeWorkflow",
                ".github/workflows/ci.yml", Sha, "completed", "success", new string('B', 64)));
    }

    private sealed class FakeTransport : IGitHubArtifactHttpTransport
    {
        internal byte[] Archive { get; init; } = [];
        internal Uri Redirect { get; init; } = new(StorageUrl);
        internal bool OmitRedirect { get; init; }
        internal HttpStatusCode ApiStatus { get; init; } = HttpStatusCode.Redirect;
        internal HttpStatusCode StorageStatus { get; init; } = HttpStatusCode.OK;
        internal int ApiRequests { get; private set; }
        internal int StorageRequests { get; private set; }
        internal Uri? ObservedApi { get; private set; }
        internal Uri? ObservedStorage { get; private set; }
        internal string? ObservedToken { get; private set; }

        public Task<HttpResponseMessage> GetApiAsync(Uri uri, string? token,
            CancellationToken cancellationToken)
        {
            ApiRequests++;
            ObservedApi = uri;
            ObservedToken = token;
            HttpResponseMessage response = new(ApiStatus);
            if (!OmitRedirect && ApiStatus == HttpStatusCode.Redirect)
            {
                response.Headers.Location = Redirect;
            }
            return Task.FromResult(response);
        }

        public Task<HttpResponseMessage> GetStorageAsync(Uri uri, CancellationToken cancellationToken)
        {
            StorageRequests++;
            ObservedStorage = uri;
            return Task.FromResult(new HttpResponseMessage(StorageStatus)
            {
                Content = new ByteArrayContent(Archive)
            });
        }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition)
        {
            throw new UnreachableException(reason);
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
        throw new UnreachableException("Expected failure " + typeof(T).Name);
    }
}
