using System.Reflection;
using System.Text.Json;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Campaigns;
#pragma warning restore IDE0130

public static class FuzzSeedCampaign
{
    public static async Task<int> RunAsync(string layer, string outputRoot, CancellationToken ct)
    {
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                                 "CMakeWorkflow.Campaigns.FuzzCorpus.seeds.json")
                             ?? throw new IOException("Embedded fuzz seed catalog is missing.");
        using JsonDocument catalog = await JsonDocument.ParseAsync(resource, cancellationToken: ct);
        if (!catalog.RootElement.TryGetProperty(layer, out JsonElement seeds))
        {
            throw new ArgumentException($"Unknown fuzz layer '{layer}'. Available: " +
                                        string.Join(", ", catalog.RootElement.EnumerateObject().Select(x => x.Name)));
        }

        outputRoot = Path.GetFullPath(outputRoot);
        string directory = Path.Combine(outputRoot, layer);
        _ = Directory.CreateDirectory(directory);
        int written = 0;
        foreach (JsonProperty seed in seeds.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, seed.Name);
            byte[] bytes = Convert.FromHexString(seed.Value.GetString()!);
            if (File.Exists(path))
            {
                if (!(await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(bytes))
                {
                    throw new IOException($"Existing seed differs from catalog: {path}");
                }

                continue;
            }

            await File.WriteAllBytesAsync(path, bytes, ct);
            written++;
        }

        Console.WriteLine($"{layer}: {seeds.EnumerateObject().Count()} seeds, {written} created in {directory}");
        return 0;
    }
}