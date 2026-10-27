using System.Reflection;
using System.Text.Json;

namespace CMakeWorkflow.Campaigns;

public static class FuzzSeedCampaign
{
    public static async Task<int> RunAsync(string layer, string outputRoot, CancellationToken ct)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "CMakeWorkflow.Campaigns.FuzzCorpus.seeds.json")
            ?? throw new IOException("Embedded fuzz seed catalog is missing.");
        using var catalog = await JsonDocument.ParseAsync(resource, cancellationToken: ct);
        if (!catalog.RootElement.TryGetProperty(layer, out var seeds))
            throw new ArgumentException($"Unknown fuzz layer '{layer}'. Available: " +
                string.Join(", ", catalog.RootElement.EnumerateObject().Select(x => x.Name)));
        outputRoot = Path.GetFullPath(outputRoot);
        var directory = Path.Combine(outputRoot, layer);
        Directory.CreateDirectory(directory);
        var written = 0;
        foreach (var seed in seeds.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, seed.Name);
            var bytes = Convert.FromHexString(seed.Value.GetString()!);
            if (File.Exists(path))
            {
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                    throw new IOException($"Existing seed differs from catalog: {path}");
                continue;
            }
            await File.WriteAllBytesAsync(path, bytes, ct);
            written++;
        }
        Console.WriteLine($"{layer}: {seeds.EnumerateObject().Count()} seeds, {written} created in {directory}");
        return 0;
    }
}
