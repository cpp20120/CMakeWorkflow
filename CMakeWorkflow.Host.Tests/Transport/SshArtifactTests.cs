using System.Security.Cryptography;
using CMakeWorkflow.Client;
using StreamJsonRpc;

// Read-only artifact transfer through the same bridge used by the durable workflow tests.
internal static class SshArtifactTests
{
    private static void Check(bool condition, string message) => SshTransportTests.Check(condition, message);

    internal static async Task RunAsync()
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Artifact fixture requires POSIX, CMake and Ninja.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var ct = deadline.Token;
        await using var host = new TransportTestHost();
        await host.ReadyAsync(ct);
        var fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "artifacts", ct);
        string operationId;
        using (var bridge = new SshWorkflowConnection(host.Endpoint, fixture.Workspace))
        {
            await using var client = await bridge.ConnectAsync(ct);
            var op = await client.Workflows.StartAsync(fixture.Request, ct);
            operationId = op.OperationId;
            await fixture.WaitForStartAsync(ct);
            await SshTransportTests.RejectAsync<RemoteInvocationException>(() => op.ListArtifactsAsync(ct));
        }
        fixture.Finish();
        using (var bridge = new SshWorkflowConnection(host.Endpoint, fixture.Workspace))
        {
            await using var client = await bridge.ConnectAsync(ct);
            var op = client.Operations.GetHandle(operationId);
            Check((await op.WaitForCompletionAsync(ct: ct)).State == "succeeded", "Artifact fixture did not finish.");
            var catalog = await op.ListArtifactsAsync(ct);
            Check(catalog.OperationId == operationId && catalog.ReportSha256.Length == 64,
                "Invalid operation-bound artifact identity.");
            var report = catalog.Artifacts.Single(x => x.ArtifactId == "report");
            Check(report.Size > 0 && report.Sha256 == catalog.ReportSha256,
                "Report SHA-256 does not match durable finalization.");
            Check(catalog.Artifacts.Any(x => x.Kind == "stdout") && catalog.Artifacts.Any(x => x.Kind == "stderr"),
                "Node logs missing from catalog.");

            await using var received = new MemoryStream();
            long offset = 0;
            do
            {
                var chunk = await op.ReadArtifactChunkAsync(report, offset, 47, ct);
                Check(chunk.Offset == offset && chunk.TotalSize == report.Size &&
                      chunk.LastWriteUtcTicks == report.LastWriteUtcTicks,
                    "Chunk identity or offset mismatch.");
                await received.WriteAsync(chunk.Content, ct);
                offset += chunk.Content.Length;
                Check(chunk.Eof == (offset == report.Size) &&
                      (chunk.Content.Length > 0 || chunk.Eof), "Chunk did not advance.");
            } while (offset < report.Size);
            Check(Convert.ToHexString(SHA256.HashData(received.ToArray())) == report.Sha256,
                "Downloaded report digest mismatch.");
            await SshTransportTests.RejectAsync<RemoteInvocationException>(
                () => op.ReadArtifactChunkAsync(report, -1, 10, ct));
            await SshTransportTests.RejectAsync<RemoteInvocationException>(
                () => op.ReadArtifactChunkAsync(report, 0, 65537, ct));
            await SshTransportTests.RejectAsync<RemoteInvocationException>(
                () => op.ReadArtifactChunkAsync(report with { ArtifactId = "../outside" }, 0, 10, ct));

            var stdout = catalog.Artifacts.First(x => x.Kind == "stdout");
            var stdoutPath = Path.Combine(fixture.Request.ResultsDirectory,
                stdout.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            File.AppendAllText(stdoutPath, "altered after listing");
            await SshTransportTests.RejectAsync<RemoteInvocationException>(
                () => op.ReadArtifactChunkAsync(stdout, 0, 16, ct));
            Check(fixture.Effects().SequenceEqual(["started", "finished"]),
                "Reading artifacts redispatched the workflow.");
        }
        Console.WriteLine("SSH artifact tests passed: report digest, chunk ranges, stale files, IDs and reconnect.");
    }
}
