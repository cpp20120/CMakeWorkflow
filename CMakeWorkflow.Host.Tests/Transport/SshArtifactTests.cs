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
        {
            throw new PlatformNotSupportedException("Artifact fixture requires POSIX, CMake and Ninja.");
        }

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(75));
        CancellationToken ct = deadline.Token;
        await using TransportTestHost host = new();
        await host.ReadyAsync(ct);
        RemoteWorkflowFixture fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "artifacts", ct);
        string operationId;
        using (SshWorkflowConnection bridge = new(host.Endpoint, fixture.Workspace))
        {
            await using CMakeWorkflowClient client = await bridge.ConnectAsync(ct);
            WorkflowOperation op = await client.Workflows.StartAsync(fixture.Request, ct);
            operationId = op.OperationId;
            await fixture.WaitForStartAsync(ct);
            _ = await SshTransportTests.RejectAsync<RemoteInvocationException>(() => op.ListArtifactsAsync(ct));
        }
        fixture.Finish();
        using (SshWorkflowConnection bridge = new(host.Endpoint, fixture.Workspace))
        {
            await using CMakeWorkflowClient client = await bridge.ConnectAsync(ct);
            WorkflowOperation op = client.Operations.GetHandle(operationId);
            Check((await op.WaitForCompletionAsync(ct: ct)).State == "succeeded", "Artifact fixture did not finish.");
            OperationArtifactList catalog = await op.ListArtifactsAsync(ct);
            Check(catalog.OperationId == operationId && catalog.ReportSha256.Length == 64,
                "Invalid operation-bound artifact identity.");
            OperationArtifactInfo report = catalog.Artifacts.Single(x => x.ArtifactId == "report");
            Check(report.Size > 0 && report.Sha256 == catalog.ReportSha256,
                "Report SHA-256 does not match durable finalization.");
            Check(catalog.Artifacts.Any(x => x.Kind == "stdout") && catalog.Artifacts.Any(x => x.Kind == "stderr"),
                "Node logs missing from catalog.");

            await using MemoryStream received = new();
            long offset = 0;
            do
            {
                OperationArtifactChunk chunk = await op.ReadArtifactChunkAsync(report, offset, 47, ct);
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
            _ = await SshTransportTests.RejectAsync<RemoteInvocationException>(
                () => op.ReadArtifactChunkAsync(report, -1, 10, ct));
            _ = await SshTransportTests.RejectAsync<RemoteInvocationException>(
                () => op.ReadArtifactChunkAsync(report, 0, 65537, ct));
            _ = await SshTransportTests.RejectAsync<RemoteInvocationException>(
                () => op.ReadArtifactChunkAsync(report with { ArtifactId = "../outside" }, 0, 10, ct));

            OperationArtifactInfo stdout = catalog.Artifacts.First(x => x.Kind == "stdout");
            string stdoutPath = Path.Combine(fixture.Request.ResultsDirectory,
                stdout.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(stdoutPath, "altered after listing", CancellationToken.None);
            _ = await SshTransportTests.RejectAsync<RemoteInvocationException>(
                () => op.ReadArtifactChunkAsync(stdout, 0, 16, ct));
            Check(fixture.Effects().SequenceEqual(["started", "finished"]),
                "Reading artifacts redispatched the workflow.");
        }
        Console.WriteLine("SSH artifact tests passed: report digest, chunk ranges, stale files, IDs and reconnect.");
    }
}
