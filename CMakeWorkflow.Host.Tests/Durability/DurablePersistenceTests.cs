using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow;

internal static class DurablePersistenceTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("neutral durable checkpoint bytes\n");
    private const string PayloadSha256 = "1B9050038320822E83438F06A83FB4D635E22E201093FA4B3B9801A93F166FC2";

    public static async Task RunAsync(string root)
    {
        await WritesExactBytesAndReplacesOrphanTemporary(root);
        await InjectedFlushFailurePreservesOriginal(root);
        await InjectedPostReplaceFailureLeavesNewCheckpoint(root);
        await FailedReplacePreservesDestinationAndTemporaryBytes(root);
        await FailedTemporaryOpenPreservesOriginal(root);
        await CancellationPreservesOriginal(root);
        await RealWriteFailurePreservesOriginal(root);
        await ReportSavePreservesSerializedBytes(root);
        Console.WriteLine("Durable byte persistence checks passed.");
    }

    private static async Task WritesExactBytesAndReplacesOrphanTemporary(string root)
    {
        var path = Path.Combine(root, "byte-write", "run.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await File.WriteAllTextAsync(temporary, "orphaned previous temporary bytes");

        await WorkflowCheckpoint.WriteDurableBytesAsync(path, Payload, CancellationToken.None);

        Check(File.ReadAllBytes(path).SequenceEqual(Payload), "Writer changed the supplied byte payload.");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == PayloadSha256,
            "Writer checkpoint SHA-256 changed.");
        Check(!File.Exists(temporary), "Writer left a stale temporary file after replacement.");
    }

    private static async Task InjectedFlushFailurePreservesOriginal(string root)
    {
        var path = Path.Combine(root, "flush-failure", "run.json");
        var original = await CreateCheckpoint(path);
        var error = await InjectedFailure(path, "checkpoint-flushed");

        Check(error.Message == "Injected checkpoint I/O failure at checkpoint-flushed",
            "Post-flush injected error changed.");
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Post-flush failure replaced the original checkpoint.");
        Check(File.ReadAllBytes(path + ".tmp").SequenceEqual(Payload),
            "Post-flush failure changed the flushed temporary checkpoint.");
    }

    private static async Task InjectedPostReplaceFailureLeavesNewCheckpoint(string root)
    {
        if (OperatingSystem.IsWindows()) return;

        var path = Path.Combine(root, "replace-failure", "run.json");
        await CreateCheckpoint(path);
        var error = await InjectedFailure(path, "checkpoint-replaced");

        Check(error.Message == "Injected checkpoint I/O failure at checkpoint-replaced",
            "Post-replace injected error changed.");
        Check(File.ReadAllBytes(path).SequenceEqual(Payload),
            "Post-replace failure did not leave the new checkpoint visible.");
        Check(!File.Exists(path + ".tmp"), "Post-replace failure recreated the temporary file.");
    }

    private static async Task FailedReplacePreservesDestinationAndTemporaryBytes(string root)
    {
        var path = Path.Combine(root, "blocked-replacement", "run.json");
        Directory.CreateDirectory(path);
        var sentinel = Path.Combine(path, "original-checkpoint");
        await File.WriteAllTextAsync(sentinel, "preserve this destination");

        await ThrowsIOException(() => WorkflowCheckpoint.WriteDurableBytesAsync(
            path, Payload, CancellationToken.None));

        Check(Directory.Exists(path) && File.ReadAllText(sentinel) == "preserve this destination",
            "Failed replacement changed the original destination.");
        Check(File.ReadAllBytes(path + ".tmp").SequenceEqual(Payload),
            "Failed replacement changed the durable temporary payload.");
    }

    private static async Task FailedTemporaryOpenPreservesOriginal(string root)
    {
        var path = Path.Combine(root, "blocked-temporary", "run.json");
        var original = await CreateCheckpoint(path);
        Directory.CreateDirectory(path + ".tmp");

        await ThrowsIOExceptionOrUnauthorized(() => WorkflowCheckpoint.WriteDurableBytesAsync(
            path, Payload, CancellationToken.None));

        Check(File.ReadAllBytes(path).SequenceEqual(original), "Temporary-file open failure changed the original.");
        Check(Directory.Exists(path + ".tmp"), "Temporary-file open failure removed the blocking entry.");
    }

    private static async Task CancellationPreservesOriginal(string root)
    {
        var path = Path.Combine(root, "canceled-write", "run.json");
        var original = await CreateCheckpoint(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await WorkflowCheckpoint.WriteDurableBytesAsync(path, Payload, cancellation.Token);
            throw new Exception("Canceled write unexpectedly committed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Check(File.ReadAllBytes(path).SequenceEqual(original), "Canceled write changed the original checkpoint.");
        Check(File.Exists(path + ".tmp"), "Canceled write no longer leaves the opened temporary path.");
    }

    private static async Task RealWriteFailurePreservesOriginal(string root)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/full")) return;

        var path = Path.Combine(root, "device-write-failure", "run.json");
        var original = await CreateCheckpoint(path);
        var temporary = path + ".tmp";
        File.CreateSymbolicLink(temporary, "/dev/full");

        await ThrowsIOException(() => WorkflowCheckpoint.WriteDurableBytesAsync(
            path, Payload, CancellationToken.None));

        Check(File.ReadAllBytes(path).SequenceEqual(original), "Device write failure replaced the original.");
        Check(new FileInfo(temporary).LinkTarget == "/dev/full", "Device write failure changed the temporary link.");
        File.Delete(temporary);
    }

    private static async Task ReportSavePreservesSerializedBytes(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "report-save")).FullName;
        var report = new WorkflowReport
        {
            Workflow = "workflow.json",
            ManifestHash = new string('A', 64),
            StartedAt = DateTimeOffset.UnixEpoch,
            Status = "running",
            Nodes = [new NodeResult { Id = "node", Kind = "invoke", Status = "running" }]
        };
        var expected = JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);

        await WorkflowCheckpoint.SaveAsync(report, directory, CancellationToken.None);

        var actual = await File.ReadAllBytesAsync(Path.Combine(directory, "run.json"));
        Check(actual.SequenceEqual(expected), "Report checkpoint serialization bytes changed.");
        Check(Convert.ToHexString(SHA256.HashData(actual)) == Convert.ToHexString(SHA256.HashData(expected)),
            "Report checkpoint digest is not the exact serialized-byte SHA-256.");
    }

    private static async Task<byte[]> CreateCheckpoint(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Encoding.UTF8.GetBytes("original checkpoint bytes\n");
        await File.WriteAllBytesAsync(path, original);
        return original;
    }

    private static async Task<IOException> InjectedFailure(string path, string point)
    {
        var (priorFailure, priorNode) = SetFault(point, "node");
        try
        {
            try
            {
                await WorkflowCheckpoint.WriteDurableBytesAsync(path, Payload, CancellationToken.None,
                    "node", injectRecoveryFaults: true);
            }
            catch (IOException error)
            {
                return error;
            }

            throw new Exception($"Expected injected failure at {point}.");
        }
        finally
        {
            RestoreFault(priorFailure, priorNode);
        }
    }

    private static (string? Failure, string? Node) SetFault(string point, string node)
    {
        var previous = (Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_FAILURE"),
            Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_NODE"));
        Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TEST_FAILURE", point);
        Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TEST_NODE", node);
        return previous;
    }

    private static void RestoreFault(string? failure, string? node)
    {
        Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TEST_FAILURE", failure);
        Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TEST_NODE", node);
    }

    private static async Task ThrowsIOException(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (IOException)
        {
            return;
        }

        throw new Exception("Expected IOException.");
    }

    private static async Task ThrowsIOExceptionOrUnauthorized(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        throw new Exception("Expected IOException or UnauthorizedAccessException.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
