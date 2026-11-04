using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow;

internal static class DurableByteWriterTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("neutral durable checkpoint bytes\n");
    private const string PayloadSha256 = "1B9050038320822E83438F06A83FB4D635E22E201093FA4B3B9801A93F166FC2";

    public static async Task RunAsync(string root)
    {
        await CallbacksObserveDurabilityBoundaries(root);
        await CallbackFailureIsPropagatedAndPreventsReplacement(root);
        await CancellationAfterFlushPreventsReplacement(root);
        Console.WriteLine("Neutral durable byte writer checks passed.");
    }

    private static async Task CallbacksObserveDurabilityBoundaries(string root)
    {
        var path = Path.Combine(root, "neutral-callbacks", "checkpoint.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Encoding.UTF8.GetBytes("original");
        await File.WriteAllBytesAsync(path, original);
        var surrounding = new byte[Payload.Length + 4];
        Array.Fill(surrounding, (byte)0xEE);
        Payload.CopyTo(surrounding.AsSpan(2, Payload.Length));
        var calls = new List<string>();

        await DurableByteWriter.WriteAsync(path, surrounding.AsMemory(2, Payload.Length), CancellationToken.None,
            afterFlushed: () =>
            {
                calls.Add("flushed");
                Check(File.ReadAllBytes(path + ".tmp").SequenceEqual(Payload),
                    "Flush callback did not see the complete temporary bytes.");
                Check(File.ReadAllBytes(path).SequenceEqual(original),
                    "Flush callback ran after replacing the original destination.");
                return Task.CompletedTask;
            },
            afterReplaced: () =>
            {
                calls.Add("replaced");
                Check(File.ReadAllBytes(path).SequenceEqual(Payload),
                    "Replace callback did not see the committed payload.");
                Check(!File.Exists(path + ".tmp"), "Replace callback saw a temporary path after rename.");
                return Task.CompletedTask;
            },
            beforeDirectorySync: () =>
            {
                calls.Add("directory-sync");
                Check(File.ReadAllBytes(path).SequenceEqual(Payload),
                    "Directory sync callback ran before replacement.");
                return Task.CompletedTask;
            });

        var expectedCalls = OperatingSystem.IsWindows()
            ? new[] { "flushed" }
            : ["flushed", "replaced", "directory-sync"];
        Check(calls.SequenceEqual(expectedCalls), "Neutral writer callback order changed.");
        var actual = await File.ReadAllBytesAsync(path);
        Check(actual.SequenceEqual(Payload), "Neutral writer did not write the selected memory slice.");
        Check(Convert.ToHexString(SHA256.HashData(actual)) == PayloadSha256,
            "Neutral writer changed the exact payload SHA-256.");
    }

    private static async Task CallbackFailureIsPropagatedAndPreventsReplacement(string root)
    {
        var path = Path.Combine(root, "neutral-callback-failure", "checkpoint.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Encoding.UTF8.GetBytes("original");
        await File.WriteAllBytesAsync(path, original);
        var expected = new IOException("neutral callback failure");
        try
        {
            await DurableByteWriter.WriteAsync(path, Payload, CancellationToken.None,
                afterFlushed: () => Task.FromException(expected));
            throw new Exception("Writer swallowed the callback exception.");
        }
        catch (IOException actual) when (ReferenceEquals(actual, expected))
        {
        }

        Check(File.ReadAllBytes(path).SequenceEqual(original), "Callback failure replaced the original.");
        Check(File.ReadAllBytes(path + ".tmp").SequenceEqual(Payload),
            "Callback failure changed the flushed temporary bytes.");
    }

    private static async Task CancellationAfterFlushPreventsReplacement(string root)
    {
        var path = Path.Combine(root, "neutral-canceled-callback", "checkpoint.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = Encoding.UTF8.GetBytes("original");
        await File.WriteAllBytesAsync(path, original);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await DurableByteWriter.WriteAsync(path, Payload, cancellation.Token,
                afterFlushed: () =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                });
            throw new Exception("Writer committed after cancellation at the flush boundary.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Check(File.ReadAllBytes(path).SequenceEqual(original), "Post-flush cancellation replaced the original.");
        Check(File.ReadAllBytes(path + ".tmp").SequenceEqual(Payload),
            "Post-flush cancellation changed the flushed temporary bytes.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
