using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Reporting;

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
        string path = Path.Combine(root, "byte-write", "run.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await File.WriteAllTextAsync(temporary, "orphaned previous temporary bytes");

        await WorkflowCheckpoint.WriteDurableBytesAsync(path, Payload, CancellationToken.None);

        Check((await File.ReadAllBytesAsync(path, CancellationToken.None)).SequenceEqual(Payload), "Writer changed the supplied byte payload.");
        Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, CancellationToken.None))) == PayloadSha256,
            "Writer checkpoint SHA-256 changed.");
        Check(!File.Exists(temporary), "Writer left a stale temporary file after replacement.");
    }

    private static async Task InjectedFlushFailurePreservesOriginal(string root)
    {
        string path = Path.Combine(root, "flush-failure", "run.json");
        byte[] original = await CreateCheckpoint(path);
        IOException error = await InjectedFailure(path, "checkpoint-flushed");

        Check(error.Message == "Injected checkpoint I/O failure at checkpoint-flushed",
            "Post-flush injected error changed.");
        Check((await File.ReadAllBytesAsync(path, CancellationToken.None)).SequenceEqual(original), "Post-flush failure replaced the original checkpoint.");
        Check((await File.ReadAllBytesAsync(path + ".tmp", CancellationToken.None)).SequenceEqual(Payload),
            "Post-flush failure changed the flushed temporary checkpoint.");
    }

    private static async Task InjectedPostReplaceFailureLeavesNewCheckpoint(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(root, "replace-failure", "run.json");
        _ = await CreateCheckpoint(path);
        IOException error = await InjectedFailure(path, "checkpoint-replaced");

        Check(error.Message == "Injected checkpoint I/O failure at checkpoint-replaced",
            "Post-replace injected error changed.");
        Check((await File.ReadAllBytesAsync(path, CancellationToken.None)).SequenceEqual(Payload),
            "Post-replace failure did not leave the new checkpoint visible.");
        Check(!File.Exists(path + ".tmp"), "Post-replace failure recreated the temporary file.");
    }

    private static async Task FailedReplacePreservesDestinationAndTemporaryBytes(string root)
    {
        string path = Path.Combine(root, "blocked-replacement", "run.json");
        _ = Directory.CreateDirectory(path);
        string sentinel = Path.Combine(path, "original-checkpoint");
        await File.WriteAllTextAsync(sentinel, "preserve this destination");

        await ThrowsIOException(() => WorkflowCheckpoint.WriteDurableBytesAsync(
            path, Payload, CancellationToken.None));

        Check(Directory.Exists(path) && (await File.ReadAllTextAsync(sentinel, CancellationToken.None)) == "preserve this destination",
            "Failed replacement changed the original destination.");
        Check((await File.ReadAllBytesAsync(path + ".tmp", CancellationToken.None)).SequenceEqual(Payload),
            "Failed replacement changed the durable temporary payload.");
    }

    private static async Task FailedTemporaryOpenPreservesOriginal(string root)
    {
        string path = Path.Combine(root, "blocked-temporary", "run.json");
        byte[] original = await CreateCheckpoint(path);
        _ = Directory.CreateDirectory(path + ".tmp");

        await ThrowsIOExceptionOrUnauthorized(() => WorkflowCheckpoint.WriteDurableBytesAsync(
            path, Payload, CancellationToken.None));

        Check((await File.ReadAllBytesAsync(path, CancellationToken.None)).SequenceEqual(original), "Temporary-file open failure changed the original.");
        Check(Directory.Exists(path + ".tmp"), "Temporary-file open failure removed the blocking entry.");
    }

    private static async Task CancellationPreservesOriginal(string root)
    {
        string path = Path.Combine(root, "canceled-write", "run.json");
        byte[] original = await CreateCheckpoint(path);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        try
        {
            await WorkflowCheckpoint.WriteDurableBytesAsync(path, Payload, cancellation.Token);
            throw new System.Diagnostics.UnreachableException("Canceled write unexpectedly committed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Check((await File.ReadAllBytesAsync(path, CancellationToken.None)).SequenceEqual(original), "Canceled write changed the original checkpoint.");
        Check(File.Exists(path + ".tmp"), "Canceled write no longer leaves the opened temporary path.");
    }

    private static async Task RealWriteFailurePreservesOriginal(string root)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/full"))
        {
            return;
        }

        string path = Path.Combine(root, "device-write-failure", "run.json");
        byte[] original = await CreateCheckpoint(path);
        string temporary = path + ".tmp";
        _ = File.CreateSymbolicLink(temporary, "/dev/full");

        await ThrowsIOException(() => WorkflowCheckpoint.WriteDurableBytesAsync(
            path, Payload, CancellationToken.None));

        Check((await File.ReadAllBytesAsync(path, CancellationToken.None)).SequenceEqual(original), "Device write failure replaced the original.");
        Check(new FileInfo(temporary).LinkTarget == "/dev/full", "Device write failure changed the temporary link.");
        File.Delete(temporary);
    }

    private static async Task ReportSavePreservesSerializedBytes(string root)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "report-save")).FullName;
        WorkflowReport report = new()
        {
            Workflow = "workflow.json",
            ManifestHash = new string('A', 64),
            StartedAt = DateTimeOffset.UnixEpoch,
            Status = "running",
            Nodes = [new NodeResult { Id = "node", Kind = "invoke", Status = "running" }]
        };
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(report, WorkflowJson.Default.WorkflowReport);

        await WorkflowCheckpoint.SaveAsync(report, directory, CancellationToken.None);

        byte[] actual = await File.ReadAllBytesAsync(Path.Combine(directory, "run.json"));
        Check(actual.SequenceEqual(expected), "Report checkpoint serialization bytes changed.");
        Check(Convert.ToHexString(SHA256.HashData(actual)) == Convert.ToHexString(SHA256.HashData(expected)),
            "Report checkpoint digest is not the exact serialized-byte SHA-256.");
    }

    private static async Task<byte[]> CreateCheckpoint(string path)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] original = Encoding.UTF8.GetBytes("original checkpoint bytes\n");
        await File.WriteAllBytesAsync(path, original);
        return original;
    }

    private static async Task<IOException> InjectedFailure(string path, string point)
    {
        (string? priorFailure, string? priorNode) = SetFault(point, "node");
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

            throw new System.Diagnostics.UnreachableException($"Expected injected failure at {point}.");
        }
        finally
        {
            RestoreFault(priorFailure, priorNode);
        }
    }

    private static (string? Failure, string? Node) SetFault(string point, string node)
    {
        (string?, string?) previous = (Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TEST_FAILURE"),
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

        throw new System.Diagnostics.UnreachableException("Expected IOException.");
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

        throw new System.Diagnostics.UnreachableException("Expected IOException or UnauthorizedAccessException.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(message);
        }
    }
}
