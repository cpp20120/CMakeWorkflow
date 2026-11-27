using System.Diagnostics;
using System.Globalization;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Binary.Capnp.Benchmarks;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Storage.Documents;

int[] sizes = args is ["--cold", string requested]
    ? [int.Parse(requested, CultureInfo.InvariantCulture)]
    : [4, 65, 256, 512];
bool cold = args.Length != 0;
DirectoryInfo directory = Directory.CreateTempSubdirectory("cmwf-doc-bench-");
try
{
    foreach (int size in sizes)
    {
        await MeasureAsync(Path.Combine(directory.FullName, size.ToString(CultureInfo.InvariantCulture)), size, cold);
    }

    using Process self = Process.GetCurrentProcess();
    Console.WriteLine($"peak_process_rss_bytes={self.PeakWorkingSet64}");
}
finally
{
    directory.Delete(recursive: true);
}

static async Task MeasureAsync(string root, int size, bool cold)
{
    Dictionary<string, List<(double Microseconds, long Allocated)>> results = new(StringComparer.Ordinal);
    System.Collections.Concurrent.ConcurrentDictionary<StoreFault, long> phase = new();
    long init = Stopwatch.GetTimestamp();
    DocumentStore store = await DocumentStore.InitializeAsync(root, CancellationToken.None,
        point => phase[point] = Stopwatch.GetTimestamp());
    Record("initialize", Elapsed(init), 0);
    int rounds = cold ? 1 : 5;
    int binarySize = 0, jsonSize = 0;
    for (int round = cold ? 0 : -1; round < rounds; round++)
    {
        bool report = round >= 0;
        WorkflowDocument workflow = MatrixDocuments.Create(size);
        WorkflowContext context = workflow.Contexts.Values.First();
        context.BuildDirectory += "-" + round.ToString(CultureInfo.InvariantCulture);
        byte[] staging = Time("binary_encode_with_normalization", () => WorkflowEncoding.Encode(workflow), report);
        byte[] canonical = Time("canonicalize", () => CapnpWire.Canonicalize(staging), report);
        byte[] frame = Time("frame", () => CapnpWire.Frame(canonical), report);
        _ = Time("hash_exact_and_canonical", () => (DocumentDescriptor.Hash(frame), CapnpWire.HashCanonical(canonical)), report);
        byte[] json = Time("json_encode", () => WorkflowManifestBytes.Serialize(workflow), report);
        binarySize = frame.Length;
        jsonSize = json.Length;
        string file = Path.Combine(root, $"baseline-{round}.json");
        _ = await TimeAsync("json_recipe_write_different_durability", async () =>
        {
            await WorkflowManifestBytes.WriteNewAsync(file, workflow, CancellationToken.None);
            return true;
        }, report);
        phase.Clear();
        DocumentDescriptor doc = await TimeAsync("binary_document_publish", () => store.PublishAsync(workflow), report);
        Phase("blob_stage_write", StoreFault.BeforeStage, StoreFault.AfterWrite, report);
        Phase("file_flush", StoreFault.AfterWrite, StoreFault.AfterFlush, report);
        Phase("blob_create_only_sync", StoreFault.BeforePublish, StoreFault.AfterPublish, report);
        Phase("blob_reverify_and_insert", StoreFault.AfterPublish, StoreFault.BeforeCommit, report);
        Phase("sqlite_commit", StoreFault.BeforeCommit, StoreFault.AfterCommit, report);
        _ = await TimeAsync("binary_duplicate_publish", () => store.PublishAsync(workflow), report);
        LegacyDigest legacy = new(DocumentDescriptor.Hash(json));
        DocumentDescriptor legacyDocument = await TimeAsync("legacy_document_import", () => store.ImportJsonAsync(json, legacy), report);
        using DocumentSession legacyExact = await TimeAsync("legacy_exact_only_buffered_open", () =>
            store.OpenAsync(legacyDocument.Id, verification: VerificationMode.StoredBytes), report);
        using DocumentSession legacyVerified = await TimeAsync("legacy_verified_buffered_open", () =>
            store.OpenAsync(legacyDocument.Id), report);

        using DocumentSession buffered = await TimeAsync("verified_buffered_open", () => store.OpenAsync(doc.Id), report);
        using DocumentSession mapped = await TimeAsync("verified_mapped_open", () => store.OpenAsync(doc.Id,
            DocumentAccess.Mapped), report);
        using DocumentSession exact = await TimeAsync("exact_only_buffered_open", () =>
            store.OpenAsync(doc.Id, verification: VerificationMode.StoredBytes), report);
        _ = Time("buffered_reused_select", () => Repeat(buffered), report, 1000);
        _ = Time("mapped_reused_select", () => Repeat(mapped), report, 1000);
        _ = Time("reused_materialize", buffered.Materialize, report);
        _ = Time("reused_traverse", () => mapped.InspectWorkflow(MatrixDocuments.Traverse), report);
        WorkflowDocument materialized = buffered.Materialize();
        _ = Time("materialized_traverse", () => IterateDto(materialized), report, 1000);

        if (round == rounds - 1)
        {
            _ = await TimeAsync("four_concurrent_verified_reads", () => Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => Task.Run(async () =>
                {
                    using DocumentSession reader = await store.OpenAsync(doc.Id, DocumentAccess.Mapped);
                    return Repeat(reader);
                }))), report);
            _ = await TimeAsync("four_concurrent_duplicate_publications", () => Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() => store.PublishAsync(workflow)))), report);
        }
    }

    long db = Directory.EnumerateFiles(root, "documents.sqlite*").Sum(path => new FileInfo(path).Length);
    CollectionResult collected = await TimeAsync("collect_unretained_documents", () => store.CollectAsync(), true);
    Console.WriteLine($"collected_documents={collected.DocumentsRemoved} collected_blobs={collected.BlobsRemoved}");
    Console.WriteLine($"nodes={size} json_bytes={jsonSize} framed_bytes={binarySize} catalog_bytes={db} measurements={rounds}");
    foreach ((string name, List<(double Microseconds, long Allocated)> observations) in results)
    {
        double[] times = [.. observations.Select(item => item.Microseconds).Order()];
        long[] allocations = [.. observations.Select(item => item.Allocated).Order()];
        Console.WriteLine(FormattableString.Invariant(
            $"{name} us={times[times.Length / 2]:F3} managed_bytes={allocations[allocations.Length / 2]}"));
    }

    T Time<T>(string name, Func<T> action, bool report, int operations = 1)
    {
        long allocation = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        T result = action();
        if (report)
        {
            Record(name, Elapsed(start) / operations, (GC.GetTotalAllocatedBytes(precise: true) - allocation) / operations);
        }

        return result;
    }

    async Task<T> TimeAsync<T>(string name, Func<Task<T>> action, bool report)
    {
        long allocation = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        T result = await action();
        if (report)
        {
            Record(name, Elapsed(start), GC.GetTotalAllocatedBytes(precise: true) - allocation);
        }

        return result;
    }

    void Record(string name, double elapsed, long bytes)
    {
        if (!results.TryGetValue(name, out List<(double, long)>? observations))
        {
            observations = [];
            results.Add(name, observations);
        }

        observations.Add((elapsed, bytes));
    }

    void Phase(string name, StoreFault from, StoreFault to, bool report)
    {
        if (report)
        {
            Record(name, Stopwatch.GetElapsedTime(phase[from], phase[to]).TotalMicroseconds, 0);
        }
    }
}

static int Repeat(DocumentSession session)
{
    int sum = 0;
    for (int i = 0; i < 1000; i++)
    {
        sum += session.InspectWorkflow(static view => view.GetNodeIdUtf8(view.NodeCount / 2).Length);
    }

    return sum;
}

static int IterateDto(WorkflowDocument document)
{
    int score = 0;
    for (int i = 0; i < 1000; i++)
    {
        score += MatrixDocuments.Traverse(document);
    }

    return score;
}

static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMicroseconds;
