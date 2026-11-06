using System.IO.Compression;
using System.Text.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using CMakeWorkflow.Campaigns.FuzzCorpus;
using CMakeWorkflow.Campaigns.WeakMemory;
using CMakeWorkflow.Core;

string root = Path.Combine(Path.GetTempPath(), "cmake-workflow-campaign-test-" + Guid.NewGuid().ToString("N"));
try
{
    _ = await FuzzSeedCampaign.RunAsync("ranges", root, CancellationToken.None);
    string layer = Path.Combine(root, "ranges");
    if (Directory.GetFiles(layer).Length != 364)
    {
        throw new System.Diagnostics.UnreachableException("Ranges seed count changed.");
    }

    byte[] boundary = await File.ReadAllBytesAsync(Path.Combine(layer, "boundary-0-0-0-0"));
    if (!boundary.AsSpan().SequenceEqual(new byte[] { 3, 6, 0, 0, 0, 0, 4, 63, 0, 1, 1, 1, 8, 1 }))
    {
        throw new System.Diagnostics.UnreachableException("Ranges seed byte layout changed.");
    }

    _ = await FuzzSeedCampaign.RunAsync("ranges", root, CancellationToken.None);
    if (Directory.GetFiles(layer).Length != 364)
    {
        throw new System.Diagnostics.UnreachableException("Seed command is not idempotent.");
    }

    await File.WriteAllBytesAsync(Path.Combine(layer, "boundary-0-0-0-0"), [0]);
    try
    {
        _ = await FuzzSeedCampaign.RunAsync("ranges", root, CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Changed seed was silently overwritten.");
    }
    catch (IOException)
    {
    }

    Assembly assembly = typeof(FuzzSeedCampaign).Assembly;
    using (Stream catalog = assembly.GetManifestResourceStream("CMakeWorkflow.Campaigns.Mutation.mutants.json")
                         ?? throw new System.Diagnostics.UnreachableException("Mutation catalog missing from assembly."))
    using (JsonDocument document = await JsonDocument.ParseAsync(catalog))
    {
        if (!document.RootElement.GetProperty("mutants").TryGetProperty("graph-drop-last-token", out _))
        {
            throw new System.Diagnostics.UnreachableException("Graph mutation missing from catalog.");
        }
    }

    using (Stream catalog = typeof(Program).Assembly.GetManifestResourceStream(
                             "CMakeWorkflow.Campaigns.Tests.weak-memory-catalog.json.gz")
                         ?? throw new System.Diagnostics.UnreachableException("Weak-memory catalog missing from assembly."))
    using (GZipStream compressed = new(catalog, CompressionMode.Decompress))
    using (JsonDocument document = await JsonDocument.ParseAsync(compressed))
    {
        JsonElement protocols = document.RootElement.GetProperty("protocols");
        if (protocols.EnumerateObject().Count() != 5 ||
            !protocols.GetProperty("shutdown").GetProperty("variants").EnumerateArray()
                .Any(x => x.GetProperty("name").GetString() == "baseline"))
        {
            throw new System.Diagnostics.UnreachableException("Weak-memory catalog is incomplete.");
        }

        if (Environment.GetEnvironmentVariable("DAGFLOW_SOURCE_DIR") is { } sourceRoot)
        {
            SourceCases cases = new(sourceRoot);
            foreach (JsonProperty protocol in protocols.EnumerateObject())
            {
                IReadOnlyList<RelacyVariant> generated = cases.Generate(protocol.Name);
                JsonElement[] expected = [.. protocol.Value.GetProperty("variants").EnumerateArray()];
                if (generated.Count != expected.Length)
                {
                    throw new System.Diagnostics.UnreachableException("Source-derived variant count changed: " + protocol.Name);
                }

                for (int i = 0; i < generated.Count; i++)
                {
                    if (generated[i].Name != expected[i].GetProperty("name").GetString()
                        || generated[i].Header != expected[i].GetProperty("header").GetString())
                    {
                        string actual = generated[i].Header;
                        string frozen = expected[i].GetProperty("header").GetString()!;
                        int first = Enumerable.Range(0, Math.Min(actual.Length, frozen.Length))
                            .FirstOrDefault(index => actual[index] != frozen[index], -1);
                        throw new System.Diagnostics.UnreachableException("Source-derived adapter differs from frozen catalog: "
                                            + protocol.Name + "/" + generated[i].Name + " at " + first
                                            + " actual=" +
                                            actual[Math.Max(0, first - 35)..Math.Min(actual.Length, first + 60)]
                                            + " frozen=" +
                                            frozen[Math.Max(0, first - 35)..Math.Min(frozen.Length, first + 60)]);
                    }

                    JsonElement[] expectedSuites = [.. expected[i].GetProperty("suites").EnumerateArray()];
                    if (generated[i].Suites.Length != expectedSuites.Length)
                    {
                        throw new System.Diagnostics.UnreachableException("Source-derived suite count changed: " + protocol.Name);
                    }

                    for (int suite = 0; suite < expectedSuites.Length; suite++)
                    {
                        if (generated[i].Suites[suite].Name != expectedSuites[suite].GetProperty("name").GetString()
                            || !generated[i].Suites[suite].Expected.SequenceEqual(
                                expectedSuites[suite].GetProperty("expected").EnumerateArray()
                                    .Select(x => x.GetString()!)))
                        {
                            throw new System.Diagnostics.UnreachableException("Source-derived expectation changed: "
                                                + protocol.Name + "/" + generated[i].Name);
                        }
                    }
                }
            }
        }
    }

    string run = Path.Combine(root, "run");
    string runCorpus = Path.Combine(run, "corpus");
    string merged = Path.Combine(root, "merged");
    _ = Directory.CreateDirectory(runCorpus);
    await File.WriteAllTextAsync(Path.Combine(runCorpus, "seed"), "seed");
    await File.WriteAllTextAsync(Path.Combine(runCorpus, "new"), "mutation");
    await File.WriteAllTextAsync(Path.Combine(run, "report.json"),
                             /*lang=json,strict*/
                             """{"Status":"succeeded","BuildDirectory":"/unused","Target":"dagflow_fuzz_ranges"}""");
    JsonObject inventory = new()
    {
        ["seed"] = await CommandExecution.Sha256Async(Path.Combine(runCorpus, "seed"), CancellationToken.None),
        ["new"] = await CommandExecution.Sha256Async(Path.Combine(runCorpus, "new"), CancellationToken.None)
    };
    await File.WriteAllTextAsync(Path.Combine(run, "final-corpus.json"), inventory.ToJsonString());
    _ = Directory.CreateDirectory(merged);
    await File.WriteAllTextAsync(Path.Combine(merged, "new"), "existing content");
    if (await FuzzRunCampaign.MergeAsync(run, merged, CancellationToken.None) != 0
        || Directory.GetFiles(merged).Length != 3
        || await File.ReadAllTextAsync(Path.Combine(merged, "new")) != "existing content")
    {
        throw new System.Diagnostics.UnreachableException("Fuzz corpus merge failed.");
    }

    _ = await FuzzRunCampaign.MergeAsync(run, merged, CancellationToken.None);
    if (Directory.GetFiles(merged).Length != 3)
    {
        throw new System.Diagnostics.UnreachableException("Fuzz corpus merge was not idempotent.");
    }

    await File.WriteAllTextAsync(Path.Combine(runCorpus, "new"), "changed after run");
    try
    {
        _ = await FuzzRunCampaign.MergeAsync(run, merged, CancellationToken.None);
        throw new System.Diagnostics.UnreachableException("Changed fuzz corpus was merged.");
    }
    catch (IOException)
    {
    }

    Console.WriteLine("Campaign checks passed.");
    return 0;
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}
