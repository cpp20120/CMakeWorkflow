using System.Diagnostics;
using System.Security.Cryptography;
using CMakeWorkflow.Core.Persistence;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static class ActionCachePersistenceMeasurements
{
    internal static async Task MeasureAsync(string parent)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));
        CancellationToken ct = deadline.Token;
        string root = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(parent),
            "cache-measure-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            ArtifactStoreFiles legacy = new(Path.Combine(root, "legacy"));
            ArtifactStoreFiles indexed = new(Path.Combine(root, "indexed"));
            await using (FileStream lease = await indexed.OpenLeaseAsync(create: true, ct, requireIndexedActions: true))
            {
                using CasReferenceIndex index = new(indexed, initialize: true);
            }

            string key = new('1', 64);
            string output = Path.Combine(root, "output");
            byte[] bytes = new byte[1024 * 1024];
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await File.WriteAllBytesAsync(output, bytes, ct);
            CasActions oldWriter = new(legacy);
            CasActions indexWriter = new(indexed);
            await oldWriter.PublishAsync(key, output, digest, ct);
            long start = Stopwatch.GetTimestamp();
            if (await indexWriter.ImportLegacyAsync(legacy, static () => Task.CompletedTask, ct) != 1)
            {
                throw new InvalidOperationException("Cache measurement import did not create one entry.");
            }

            Console.WriteLine(FormattableString.Invariant(
                $"import_ms={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3} data_bytes={bytes.Length} samples=1"));

            foreach ((string kind, CasActions actions) in new[] { ("legacy", oldWriter), ("sqlite", indexWriter) })
            {
                List<double> timings = [];
                for (int sample = 0; sample < 13; sample++)
                {
                    File.Delete(output);
                    start = Stopwatch.GetTimestamp();
                    if (!await actions.TryRestoreAsync(key, output, ct))
                    {
                        throw new InvalidOperationException("Cache measurement missed the retained entry.");
                    }

                    if (sample >= 2)
                    {
                        timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                }

                Console.WriteLine(FormattableString.Invariant(
                    $"{kind} verified_restore_ms={timings.Order().ElementAt(5):F3} samples={timings.Count}"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
