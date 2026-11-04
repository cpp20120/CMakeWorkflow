using System.Security.Cryptography;
using CMakeWorkflow.Client;

// Real Host and bridge subprocesses, with POSIX OpenSSH substituted only at the network hop.
internal static class SshDownloadTests
{
    private static void Check(bool condition, string description) => SshTransportTests.Check(condition, description);

    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    internal static async Task RunAsync()
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SSH download fixture requires POSIX, CMake and Ninja.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await using var host = new TransportTestHost();
        await host.ReadyAsync(ct);
        var fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "download", ct);
        var downloads = Path.Combine(fixture.Workspace, "local downloads");
        Directory.CreateDirectory(downloads);
        string operationId;
        OperationArtifactInfo big;
        var destination = Path.Combine(downloads, "effect.stdout");

        using (var bridge = new SshWorkflowConnection(host.Endpoint, fixture.Workspace))
        {
            await using var client = await bridge.ConnectAsync(ct);
            var operation = await client.Workflows.StartAsync(fixture.Request, ct);
            operationId = operation.OperationId;
            await fixture.WaitForStartAsync(ct);
            fixture.Finish();
            Check((await operation.WaitForCompletionAsync(ct: ct)).State == "succeeded", "Fixture did not finish.");

            var initialCatalog = await operation.ListArtifactsAsync(ct);
            var initialStdout = initialCatalog.Artifacts.Single(x => x.Kind == "stdout");
            var remoteLog = Path.Combine(fixture.Request.ResultsDirectory,
                initialStdout.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            // D2 exposes only report-bound log files; enlarge a log to test multiple
            // checkpoints rather than introducing an unauthorized arbitrary file API.
            var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 137);
            File.WriteAllBytes(remoteLog, payload);
            var catalog = await operation.ListArtifactsAsync(ct);
            big = catalog.Artifacts.Single(x => x.ArtifactId == initialStdout.ArtifactId);
            Check(big.Size == payload.Length && big.Sha256 == Convert.ToHexString(SHA256.HashData(payload)),
                "Large test artifact is not bound to the finalized operation.");

            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var progress = new SynchronousProgress(offset =>
            {
                if (offset >= 1024 * 1024) stopped.Cancel();
            });
            await SshTransportTests.RejectAsync<OperationCanceledException>(
                () => operation.DownloadArtifactAsync(big, destination, progress, stopped.Token));
            Check(!File.Exists(destination) && File.Exists(destination + ".cmwf.part") &&
                  File.Exists(destination + ".cmwf.part.meta"),
                "Interrupted download was published or discarded instead of checkpointed.");
            Check(new FileInfo(destination + ".cmwf.part").Length >= 1024 * 1024,
                "Interruption occurred before a durable prefix was committed.");
            // Simulate the bytes a crashed writer could leave after the last
            // checkpoint; resume must discard this uncommitted tail.
            await File.AppendAllTextAsync(destination + ".cmwf.part", "discard-me", ct);
        }

        // The previous SDK/SSH connection is closed: this must resume using a new
        // client and the unchanged operation ID, without calling workflow/start.
        using (var bridge = new SshWorkflowConnection(host.Endpoint, fixture.Workspace))
        {
            await using var client = await bridge.ConnectAsync(ct);
            var operation = client.Operations.GetHandle(operationId);
            var result = await operation.DownloadArtifactAsync(big, destination, ct: ct);
            Check(result == Path.GetFullPath(destination) && File.Exists(destination),
                "Resumed download did not publish its destination.");
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))) == big.Sha256,
                "Resumed download has invalid SHA-256.");
            Check(!File.Exists(destination + ".cmwf.part") &&
                  !File.Exists(destination + ".cmwf.part.meta"),
                "Successful atomic promotion retained resumable transfer state.");
            await SshTransportTests.RejectAsync<IOException>(
                () => operation.DownloadArtifactAsync(big, destination, ct: ct));

            // A checkpoint for one artifact must not be reused for a different digest.
            var damaged = Path.Combine(downloads, "tampered-prefix");
            using (var interrupt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var progress = new SynchronousProgress(offset =>
                {
                    if (offset >= 1024 * 1024) interrupt.Cancel();
                });
                await SshTransportTests.RejectAsync<OperationCanceledException>(
                    () => operation.DownloadArtifactAsync(big, damaged, progress, interrupt.Token));
            }
            await SshTransportTests.RejectAsync<InvalidOperationException>(
                () => operation.DownloadArtifactAsync(big with { Sha256 = new string('0', 64) }, damaged, ct: ct));
            Check(File.Exists(damaged + ".cmwf.part"), "Foreign checkpoint request deleted owner bytes.");

            // Prefix corruption is discovered before making further RPC reads.
            using (var stream = new FileStream(damaged + ".cmwf.part", FileMode.Open, FileAccess.ReadWrite))
            {
                var initial = stream.ReadByte();
                Check(initial >= 0, "Missing bytes in committed prefix.");
                stream.Position = 0;
                stream.WriteByte((byte)(initial ^ 0xff));
            }
            await SshTransportTests.RejectAsync<InvalidDataException>(
                () => operation.DownloadArtifactAsync(big, damaged, ct: ct));
            Check(!File.Exists(damaged) && !File.Exists(damaged + ".cmwf.part") &&
                  !File.Exists(damaged + ".cmwf.part.meta"),
                "Corrupt prefix was published or left resumable.");

            var incorrectDigest = Path.Combine(downloads, "incorrect-digest");
            await SshTransportTests.RejectAsync<InvalidDataException>(
                () => operation.DownloadArtifactAsync(big with { Sha256 = new string('0', 64) },
                    incorrectDigest, ct: ct));
            Check(!File.Exists(incorrectDigest) && !File.Exists(incorrectDigest + ".cmwf.part") &&
                  !File.Exists(incorrectDigest + ".cmwf.part.meta"),
                "Final digest mismatch published an untrusted file.");

            var report = (await operation.ListArtifactsAsync(ct)).Artifacts.Single(x => x.ArtifactId == "report");
            var reportPath = Path.Combine(downloads, "run.json");
            await operation.DownloadArtifactAsync(report, reportPath, ct: ct);
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(reportPath))) == report.Sha256,
                "Terminal report download hash mismatch.");
            Check(fixture.Effects().SequenceEqual(["started", "finished"]),
                "Download disconnected or redispatched a finalized workflow.");
        }

        Console.WriteLine("SSH download passed: resumed SHA-256-verified large file, immutable target, " +
                          "foreign checkpoint, corrupt prefix, bad digest and report.");
    }
}
