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
        {
            throw new PlatformNotSupportedException("SSH download fixture requires POSIX, CMake and Ninja.");
        }

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        CancellationToken ct = deadline.Token;
        await using TransportTestHost host = new();
        await host.ReadyAsync(ct);
        RemoteWorkflowFixture fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "download", ct);
        string downloads = Path.Combine(fixture.Workspace, "local downloads");
        _ = Directory.CreateDirectory(downloads);
        string operationId;
        OperationArtifactInfo big;
        string destination = Path.Combine(downloads, "effect.stdout");

        using (SshWorkflowConnection bridge = new(host.Endpoint, fixture.Workspace))
        {
            await using CMakeWorkflowClient client = await bridge.ConnectAsync(ct);
            WorkflowOperation operation = await client.Workflows.StartAsync(fixture.Request, ct);
            operationId = operation.OperationId;
            await fixture.WaitForStartAsync(ct);
            fixture.Finish();
            Check((await operation.WaitForCompletionAsync(ct: ct)).State == "succeeded", "Fixture did not finish.");

            OperationArtifactList initialCatalog = await operation.ListArtifactsAsync(ct);
            OperationArtifactInfo initialStdout = initialCatalog.Artifacts.Single(x => x.Kind == "stdout");
            string remoteLog = Path.Combine(fixture.Request.ResultsDirectory,
                initialStdout.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            // D2 exposes only report-bound log files; enlarge a log to test multiple
            // checkpoints rather than introducing an unauthorized arbitrary file API.
            byte[] payload = RandomNumberGenerator.GetBytes((3 * 1024 * 1024) + 137);
            await File.WriteAllBytesAsync(remoteLog, payload, CancellationToken.None);
            OperationArtifactList catalog = await operation.ListArtifactsAsync(ct);
            big = catalog.Artifacts.Single(x => x.ArtifactId == initialStdout.ArtifactId);
            Check(big.Size == payload.Length && big.Sha256 == Convert.ToHexString(SHA256.HashData(payload)),
                "Large test artifact is not bound to the finalized operation.");

            using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
            SynchronousProgress progress = new(offset =>
            {
                if (offset >= 1024 * 1024)
                {
                    stopped.Cancel();
                }
            });
            _ = await SshTransportTests.RejectAsync<OperationCanceledException>(
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
        using (SshWorkflowConnection bridge = new(host.Endpoint, fixture.Workspace))
        {
            await using CMakeWorkflowClient client = await bridge.ConnectAsync(ct);
            WorkflowOperation operation = client.Operations.GetHandle(operationId);
            string result = await operation.DownloadArtifactAsync(big, destination, ct: ct);
            Check(result == Path.GetFullPath(destination) && File.Exists(destination),
                "Resumed download did not publish its destination.");
            Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination, CancellationToken.None))) == big.Sha256,
                "Resumed download has invalid SHA-256.");
            Check(!File.Exists(destination + ".cmwf.part") &&
                  !File.Exists(destination + ".cmwf.part.meta"),
                "Successful atomic promotion retained resumable transfer state.");
            _ = await SshTransportTests.RejectAsync<IOException>(
                () => operation.DownloadArtifactAsync(big, destination, ct: ct));

            // A checkpoint for one artifact must not be reused for a different digest.
            string damaged = Path.Combine(downloads, "tampered-prefix");
            using (CancellationTokenSource interrupt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                SynchronousProgress progress = new(offset =>
                {
                    if (offset >= 1024 * 1024)
                    {
                        interrupt.Cancel();
                    }
                });
                _ = await SshTransportTests.RejectAsync<OperationCanceledException>(
                    () => operation.DownloadArtifactAsync(big, damaged, progress, interrupt.Token));
            }
            _ = await SshTransportTests.RejectAsync<InvalidOperationException>(
                () => operation.DownloadArtifactAsync(big with { Sha256 = new string('0', 64) }, damaged, ct: ct));
            Check(File.Exists(damaged + ".cmwf.part"), "Foreign checkpoint request deleted owner bytes.");

            // Prefix corruption is discovered before making further RPC reads.
            using (FileStream stream = new(damaged + ".cmwf.part", FileMode.Open, FileAccess.ReadWrite))
            {
                int initial = stream.ReadByte();
                Check(initial >= 0, "Missing bytes in committed prefix.");
                stream.Position = 0;
                stream.WriteByte((byte)(initial ^ 0xff));
            }
            _ = await SshTransportTests.RejectAsync<InvalidDataException>(
                () => operation.DownloadArtifactAsync(big, damaged, ct: ct));
            Check(!File.Exists(damaged) && !File.Exists(damaged + ".cmwf.part") &&
                  !File.Exists(damaged + ".cmwf.part.meta"),
                "Corrupt prefix was published or left resumable.");

            string incorrectDigest = Path.Combine(downloads, "incorrect-digest");
            _ = await SshTransportTests.RejectAsync<InvalidDataException>(
                () => operation.DownloadArtifactAsync(big with { Sha256 = new string('0', 64) },
                    incorrectDigest, ct: ct));
            Check(!File.Exists(incorrectDigest) && !File.Exists(incorrectDigest + ".cmwf.part") &&
                  !File.Exists(incorrectDigest + ".cmwf.part.meta"),
                "Final digest mismatch published an untrusted file.");

            OperationArtifactInfo report = (await operation.ListArtifactsAsync(ct)).Artifacts.Single(x => x.ArtifactId == "report");
            string reportPath = Path.Combine(downloads, "run.json");
            _ = await operation.DownloadArtifactAsync(report, reportPath, ct: ct);
            Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reportPath, CancellationToken.None))) == report.Sha256,
                "Terminal report download hash mismatch.");
            Check(fixture.Effects().SequenceEqual(["started", "finished"]),
                "Download disconnected or redispatched a finalized workflow.");
        }

        Console.WriteLine("SSH download passed: resumed SHA-256-verified large file, immutable target, " +
                          "foreign checkpoint, corrupt prefix, bad digest and report.");
    }
}
