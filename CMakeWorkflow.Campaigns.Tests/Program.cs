using CMakeWorkflow.Campaigns;
using CMakeWorkflow;
using System.IO.Compression;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "cmake-workflow-campaign-test-" + Guid.NewGuid().ToString("N"));
try
{
    await FuzzSeedCampaign.RunAsync("ranges", root, CancellationToken.None);
    var layer = Path.Combine(root, "ranges");
    if (Directory.GetFiles(layer).Length != 364)
        throw new Exception("Ranges seed count changed.");
    var boundary = await File.ReadAllBytesAsync(Path.Combine(layer, "boundary-0-0-0-0"));
    if (!boundary.AsSpan().SequenceEqual(new byte[] { 3, 6, 0, 0, 0, 0, 4, 63, 0, 1, 1, 1, 8, 1 }))
        throw new Exception("Ranges seed byte layout changed.");
    await FuzzSeedCampaign.RunAsync("ranges", root, CancellationToken.None);
    if (Directory.GetFiles(layer).Length != 364)
        throw new Exception("Seed command is not idempotent.");
    await File.WriteAllBytesAsync(Path.Combine(layer, "boundary-0-0-0-0"), [0]);
    try
    {
        await FuzzSeedCampaign.RunAsync("ranges", root, CancellationToken.None);
        throw new Exception("Changed seed was silently overwritten.");
    }
    catch (IOException) { }
    var assembly = typeof(FuzzSeedCampaign).Assembly;
    using (var catalog = assembly.GetManifestResourceStream("CMakeWorkflow.Campaigns.Mutation.mutants.json")
        ?? throw new Exception("Mutation catalog missing from assembly."))
    using (var document = await JsonDocument.ParseAsync(catalog))
    {
        if (!document.RootElement.GetProperty("mutants").TryGetProperty("graph-drop-last-token", out _))
            throw new Exception("Graph mutation missing from catalog.");
    }
    using (var catalog = typeof(Program).Assembly.GetManifestResourceStream(
        "CMakeWorkflow.Campaigns.Tests.weak-memory-catalog.json.gz")
        ?? throw new Exception("Weak-memory catalog missing from assembly."))
    using (var compressed = new GZipStream(catalog, CompressionMode.Decompress))
    using (var document = await JsonDocument.ParseAsync(compressed))
    {
        var protocols = document.RootElement.GetProperty("protocols");
        if (protocols.EnumerateObject().Count() != 5 ||
            !protocols.GetProperty("shutdown").GetProperty("variants").EnumerateArray()
                .Any(x => x.GetProperty("name").GetString() == "baseline"))
            throw new Exception("Weak-memory catalog is incomplete.");
        if (Environment.GetEnvironmentVariable("DAGFLOW_SOURCE_DIR") is { } sourceRoot)
        {
            var cases = new SourceCases(sourceRoot);
            foreach (var protocol in protocols.EnumerateObject())
            {
                var generated = cases.Generate(protocol.Name);
                var expected = protocol.Value.GetProperty("variants").EnumerateArray().ToArray();
                if (generated.Count != expected.Length)
                    throw new Exception("Source-derived variant count changed: " + protocol.Name);
                for (var i = 0; i < generated.Count; i++)
                {
                    if (generated[i].Name != expected[i].GetProperty("name").GetString()
                        || generated[i].Header != expected[i].GetProperty("header").GetString())
                    {
                        var actual = generated[i].Header;
                        var frozen = expected[i].GetProperty("header").GetString()!;
                        var first = Enumerable.Range(0, Math.Min(actual.Length, frozen.Length))
                            .FirstOrDefault(index => actual[index] != frozen[index], -1);
                        throw new Exception("Source-derived adapter differs from frozen catalog: "
                            + protocol.Name + "/" + generated[i].Name + " at " + first
                            + " actual=" + actual[Math.Max(0, first - 35)..Math.Min(actual.Length, first + 60)]
                            + " frozen=" + frozen[Math.Max(0, first - 35)..Math.Min(frozen.Length, first + 60)]);
                    }
                    var expectedSuites = expected[i].GetProperty("suites").EnumerateArray().ToArray();
                    if (generated[i].Suites.Length != expectedSuites.Length)
                        throw new Exception("Source-derived suite count changed: " + protocol.Name);
                    for (var suite = 0; suite < expectedSuites.Length; suite++)
                        if (generated[i].Suites[suite].Name != expectedSuites[suite].GetProperty("name").GetString()
                            || !generated[i].Suites[suite].Expected.SequenceEqual(
                                expectedSuites[suite].GetProperty("expected").EnumerateArray()
                                    .Select(x => x.GetString()!)))
                            throw new Exception("Source-derived expectation changed: "
                                + protocol.Name + "/" + generated[i].Name);
                }
            }
        }
    }
    var run = Path.Combine(root, "run");
    var runCorpus = Path.Combine(run, "corpus");
    var merged = Path.Combine(root, "merged");
    Directory.CreateDirectory(runCorpus);
    await File.WriteAllTextAsync(Path.Combine(runCorpus, "seed"), "seed");
    await File.WriteAllTextAsync(Path.Combine(runCorpus, "new"), "mutation");
    await File.WriteAllTextAsync(Path.Combine(run, "report.json"),
        """{"Status":"succeeded","BuildDirectory":"/unused","Target":"dagflow_fuzz_ranges"}""");
    var inventory = new System.Text.Json.Nodes.JsonObject
    {
        ["seed"] = await CommandExecution.Sha256Async(Path.Combine(runCorpus, "seed"), CancellationToken.None),
        ["new"] = await CommandExecution.Sha256Async(Path.Combine(runCorpus, "new"), CancellationToken.None)
    };
    await File.WriteAllTextAsync(Path.Combine(run, "final-corpus.json"), inventory.ToJsonString());
    Directory.CreateDirectory(merged);
    await File.WriteAllTextAsync(Path.Combine(merged, "new"), "existing content");
    if (await FuzzRunCampaign.MergeAsync(run, merged, CancellationToken.None) != 0
        || Directory.GetFiles(merged).Length != 3
        || await File.ReadAllTextAsync(Path.Combine(merged, "new")) != "existing content")
        throw new Exception("Fuzz corpus merge failed.");
    await FuzzRunCampaign.MergeAsync(run, merged, CancellationToken.None);
    if (Directory.GetFiles(merged).Length != 3)
        throw new Exception("Fuzz corpus merge was not idempotent.");
    await File.WriteAllTextAsync(Path.Combine(runCorpus, "new"), "changed after run");
    try
    {
        await FuzzRunCampaign.MergeAsync(run, merged, CancellationToken.None);
        throw new Exception("Changed fuzz corpus was merged.");
    }
    catch (IOException) { }
    Console.WriteLine("Campaign checks passed.");
    return 0;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
