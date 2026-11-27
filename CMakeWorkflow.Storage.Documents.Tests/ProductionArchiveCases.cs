using System.Diagnostics;
using System.Security.Cryptography;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Storage.Documents;

internal static class ProductionArchiveCases
{
    internal static async Task RunAsync(string temp, string? oracle, CancellationToken ct)
    {
        string path = Path.Combine(temp, "production");
        DocumentStore store = await DocumentStore.OpenProductionStoreAsync(path, ct);
        WorkflowDocument workflow = CompleteFixture();
        byte[] json = [.. WorkflowDocuments.Project(workflow), 10];
        LegacyDigest legacy = new(Hash(json));
        MigrationResult archive = await store.ArchiveWorkflowAsync(json, legacy, "operation", ct);
        Require(archive.Legacy.Legacy == legacy && archive.Binary.Codec == DocumentCodec.ProductionWorkflowCapnp
            && archive.Binary.Id != archive.Legacy.Id && archive.Binary.Schema == "cmwf.production-workflow");
        Require(archive.Binary.Canonical?.Value ==
            "02ca3e06ac60655a317bf0e528efca283248c1d35834443bd20cdd7d35f929cb");
        Require(await store.ArchiveWorkflowAsync(json, legacy, "operation", ct) == archive);
        DocumentStore reopened = await DocumentStore.OpenProductionStoreAsync(path, ct);
        Require(await reopened.ResolveReferenceAsync("operation/binary", ct) == archive.Binary
            && await reopened.ResolveReferenceAsync("operation/legacy", ct) == archive.Legacy
            && await reopened.FindEquivalenceAsync(archive.Legacy.Id, archive.Binary.Id, ct) == archive.NormalizedProjectionSha256);

        byte[] exactFrame;
        using (DocumentSession lease = await reopened.OpenAsync(archive.Binary.Id, DocumentAccess.Mapped, ct: ct))
        using (DocumentSession archivedJson = await reopened.OpenAsync(archive.Legacy.Id, ct: ct))
        {
            Require(lease.Verification is { CanonicalSegment: true, Wire: true, SchemaProjection: true, Graph: false });
            Require(archivedJson.ExportStoredBytes().AsSpan().SequenceEqual(json));
            exactFrame = lease.ExportStoredBytes();
            Require(Hash(exactFrame) == archive.Binary.Blob.Value);
            WorkflowDocuments.NormalizeProjection(workflow, preserveDependencyOrder: true);
            Require(lease.ExportJsonProjection().AsSpan().SequenceEqual(WorkflowDocuments.Project(workflow)));
            Require(lease.Materialize().Nodes[3].DependsOn.SequenceEqual(["configure", "inspect", "configure"]));
        }

        if (oracle is not null)
        {
            await VerifyOfficialAsync(temp, oracle, exactFrame, archive.Binary, ct);
        }

        Require(await reopened.ImportBinaryAsync(exactFrame, ct) == archive.Binary);
        Require(await reopened.PublishAsync(workflow, ct) == archive.Binary);
        await RejectAsync<InvalidDataException>(() => DocumentStore.OpenStoreAsync(path, ct));
        await RejectAsync<InvalidDataException>(() => store.ArchiveWorkflowAsync(json, new(new string('a', 64)), "invalid", ct));
        Require(await reopened.ResolveReferenceAsync("invalid/binary", ct) is null);
        workflow.Nodes[0].WorkingDirectory = "modified";
        byte[] different = WorkflowDocuments.Project(workflow);
        await RejectAsync<InvalidDataException>(() => store.ArchiveWorkflowAsync(different, new(Hash(different)), "operation", ct));
        Require(await reopened.ResolveReferenceAsync("operation/binary", ct) == archive.Binary);
        Require((await reopened.CollectAsync(ct)).BlobsRemoved == 2);

        byte[] example = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "production-benchmark.json"), ct);
        MigrationResult real = await store.ArchiveWorkflowAsync(example, new(Hash(example)), "example", ct);
        Require(real.Binary.Codec == DocumentCodec.ProductionWorkflowCapnp);

        string abortedPath = Path.Combine(temp, "production-interrupted");
        DocumentStore interrupted = await DocumentStore.InitializeAsync(abortedPath, ct, point =>
        {
            if (point == StoreFault.BeforeCommit)
            {
                throw new IOException("Injected before publication commit.");
            }
        }, production: true);
        await RejectAsync<IOException>(() => interrupted.ArchiveWorkflowAsync(json, legacy, "aborted", ct));
        DocumentStore recovered = await DocumentStore.OpenProductionStoreAsync(abortedPath, ct);
        Require(await recovered.FindAsync(archive.Binary.Id, ct) is null
            && await recovered.ResolveReferenceAsync("aborted/legacy", ct) is null);
        Require((await recovered.CollectAsync(ct)).BlobsRemoved == 2
            && (await recovered.ArchiveWorkflowAsync(json, legacy, "aborted", ct)).Binary == archive.Binary);

        WorkflowDocument old = new() { SchemaVersion = 1, Nodes = [new() { Id = "inspect", Kind = "inspect" }] };
        DocumentDescriptor schema1 = await reopened.PublishAsync(old, ct);
        using DocumentSession oldSession = await reopened.OpenAsync(schema1.Id, ct: ct);
        Require(oldSession.Materialize().SchemaVersion == 1);

        workflow.Nodes[4].TimeoutSeconds = BitConverter.UInt64BitsToDouble(0x8000_0000_0000_0000);
        WorkflowDocument exactNumeric = ProductionWorkflowCodec.Decode(ProductionWorkflowCodec.Encode(workflow));
        Require(BitConverter.DoubleToUInt64Bits(exactNumeric.Nodes[4].TimeoutSeconds!.Value) == 0x8000_0000_0000_0000);
        workflow.Nodes[4].Kind = "unknown";
        await RejectAsync<InvalidDataException>(() => reopened.PublishAsync(workflow, ct));
        Console.WriteLine($"Production schema 1/2 archives, populated contract, conflicts, retention and recovery passed. Golden {archive.Binary.Canonical}");
    }

    private static async Task VerifyOfficialAsync(string temp, string oracle, byte[] frame, DocumentDescriptor descriptor,
        CancellationToken ct)
    {
        string input = Path.Combine(temp, "production.capnp");
        string output = Path.Combine(temp, "official-production.raw");
        await File.WriteAllBytesAsync(input, frame, ct);
        ProcessStartInfo start = new(oracle);
        start.ArgumentList.Add("verify-production");
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(output);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Cap’n Proto oracle.");
        await process.WaitForExitAsync(ct);
        Require(process.ExitCode == 0 && Hash(await File.ReadAllBytesAsync(output, ct)) == descriptor.Canonical?.Value);
        Console.WriteLine("Official C++ verified the production frame and canonical digest.");
    }

    private static WorkflowDocument CompleteFixture() => new()
    {
        SchemaVersion = 2,
        Contexts =
        {
            ["z"] = new()
            {
                BuildDirectory = "build-z", Configuration = "RelWithDebInfo", ConfigurePreset = "preset",
                SourceDirectory = ".", Mode = "managed", Generator = "Ninja", ProvenancePolicy = "allowUnverified",
                CompilerCacheProfile = "local",
                Toolchain = new("cmake", "ninja", "clang", "sdk", "environment", "Ninja", "arm64", "x64")
                {
                    EnvironmentLockSha256 = new('a', 64), VcpkgProjectSha256 = new('b', 64)
                },
                BuildVariant = new(new string('c', 64), new string('d', 64)) { KitLockSha256 = new('e', 64) }
            },
            ["a"] = new() { BuildDirectory = "build-a", Configuration = "" }
        },
        Nodes =
        [
            new() { Id = "inspect", Kind = "inspect", Context = "a" },
            new() { Id = "configure", Kind = "configure", Context = "z" },
            new()
            {
                Id = "build", Kind = "build", Context = "z", Target = "bin",
                Outputs = [new() { Name = "binary", Path = "bin", Root = "run", Fresh = true }]
            },
            new()
            {
                Id = "invoke", Kind = "invoke", Context = "z", Target = "utility", HarnessDirectory = "harness",
                DependsOn = ["configure", "inspect", "configure"]
            },
            new()
            {
                Id = "run", Kind = "run", From = "build", Arguments = ["--arg", "", "λ"],
                Inputs = [new() { Path = "bin", From = "build", Artifact = "binary", Sha256 = new('f', 64), Snapshot = true }],
                WorkingDirectory = "run", Environment = { ["Z"] = "last", ["A"] = "" },
                Resources = ["exclusive-tool"], Exclusive = true, TimeoutSeconds = 0.125,
                Metrics = new("stdout", "json", [new("metric.value", "latency", "ns", "lower")]),
                BenchmarkTrial = new("baseline", "provided", [new("input", "--size", "1", null), new("mode", "--mode", "fast", "")]),
                Constraints = new() { CpuSet = "0-2" }, ActionCache = true
            },
            new()
            {
                Id = "profile", Kind = "profile", From = "build", Options = { ["mode"] = "cpu", ["backend"] = "perf" },
                Constraints = new() { CpuSet = "3", Profiler = new() { Backend = "perf", Mode = "cpu", EventSource = "cycles" } }
            },
            new()
            {
                Id = "replay", Kind = "profile_snapshot", SnapshotExecutable = "/tmp/archive-only",
                SnapshotSha256 = new('0', 64), Constraints = new()
            },
            new() { Id = "format", Kind = "format_check", Context = "z", Arguments = ["--check"] },
            new() { Id = "test", Kind = "ctest", Context = "z", TestRegex = "", LabelRegex = "smoke" }
        ]
    };

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException("Production workflow archive invariant failed.");
        }
    }
}
