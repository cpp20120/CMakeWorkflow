using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Core.Persistence;

namespace CMakeWorkflow.Host.Tests.Durability;

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
        string path = Path.Combine(root, "neutral-callbacks", "checkpoint.bin");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] original = Encoding.UTF8.GetBytes("original");
        await File.WriteAllBytesAsync(path, original);
        byte[] surrounding = new byte[Payload.Length + 4];
        Array.Fill(surrounding, (byte)0xEE);
        Payload.CopyTo(surrounding.AsSpan(2, Payload.Length));
        List<string> calls = [];

        await DurableByteWriter.WriteAsync(path, surrounding.AsMemory(2, Payload.Length), CancellationToken.None,
            afterFlushed: async () =>
            {
                calls.Add("flushed");
                Check((await File.ReadAllBytesAsync(path + ".tmp")).SequenceEqual(Payload),
                    "Flush callback did not see the complete temporary bytes.");
                Check((await File.ReadAllBytesAsync(path)).SequenceEqual(original),
                    "Flush callback ran after replacing the original destination.");
            },
            afterReplaced: async () =>
            {
                calls.Add("replaced");
                Check((await File.ReadAllBytesAsync(path)).SequenceEqual(Payload),
                    "Replace callback did not see the committed payload.");
                Check(!File.Exists(path + ".tmp"), "Replace callback saw a temporary path after rename.");
            },
            beforeDirectorySync: async () =>
            {
                calls.Add("directory-sync");
                Check((await File.ReadAllBytesAsync(path)).SequenceEqual(Payload),
                    "Directory sync callback ran before replacement.");
            });

        string[] expectedCalls = OperatingSystem.IsWindows()
            ? ["flushed"]
            : ["flushed", "replaced", "directory-sync"];
        Check(calls.SequenceEqual(expectedCalls), "Neutral writer callback order changed.");
        byte[] actual = await File.ReadAllBytesAsync(path);
        Check(actual.SequenceEqual(Payload), "Neutral writer did not write the selected memory slice.");
        Check(Convert.ToHexString(SHA256.HashData(actual)) == PayloadSha256,
            "Neutral writer changed the exact payload SHA-256.");
    }

    private static async Task CallbackFailureIsPropagatedAndPreventsReplacement(string root)
    {
        string path = Path.Combine(root, "neutral-callback-failure", "checkpoint.bin");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] original = Encoding.UTF8.GetBytes("original");
        await File.WriteAllBytesAsync(path, original);
        IOException expected = new("neutral callback failure");
        try
        {
            await DurableByteWriter.WriteAsync(path, Payload, CancellationToken.None,
                afterFlushed: () => Task.FromException(expected));
            throw new System.Diagnostics.UnreachableException("Writer swallowed the callback exception.");
        }
        catch (IOException actual) when (ReferenceEquals(actual, expected))
        {
        }

        Check((await File.ReadAllBytesAsync(path, CancellationToken.None)).SequenceEqual(original), "Callback failure replaced the original.");
        Check((await File.ReadAllBytesAsync(path + ".tmp", CancellationToken.None)).SequenceEqual(Payload),
            "Callback failure changed the flushed temporary bytes.");
    }

    private static async Task CancellationAfterFlushPreventsReplacement(string root)
    {
        string path = Path.Combine(root, "neutral-canceled-callback", "checkpoint.bin");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] original = Encoding.UTF8.GetBytes("original");
        await File.WriteAllBytesAsync(path, original);
        using CancellationTokenSource cancellation = new();
        try
        {
            await DurableByteWriter.WriteAsync(path, Payload, cancellation.Token,
                afterFlushed: cancellation.CancelAsync);
            throw new System.Diagnostics.UnreachableException("Writer committed after cancellation at the flush boundary.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Check((await File.ReadAllBytesAsync(path, CancellationToken.None)).SequenceEqual(original), "Post-flush cancellation replaced the original.");
        Check((await File.ReadAllBytesAsync(path + ".tmp", CancellationToken.None)).SequenceEqual(Payload),
            "Post-flush cancellation changed the flushed temporary bytes.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(message);
        }
    }
}