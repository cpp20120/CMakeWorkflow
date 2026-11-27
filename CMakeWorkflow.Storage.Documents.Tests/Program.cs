using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Storage.Documents;
using Microsoft.Data.Sqlite;

using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(3));
CancellationToken ct = deadline.Token;
if (args is ["child", string childPath, string operation, string argument])
{
    if (operation == "crash")
    {
        StoreFault selected = Enum.Parse<StoreFault>(argument);
        DocumentStore child = await DocumentStore.InitializeAsync(childPath, ct, point =>
        {
            if (point == selected)
            {
                Console.WriteLine("ready");
                _ = Console.ReadLine();
            }
        });
        _ = await child.PublishAsync(Fixture(), ct);
    }
    else
    {
        DocumentStore child = await DocumentStore.OpenStoreAsync(childPath, ct);
        if (operation == "hold")
        {
            using DocumentSession held = await child.OpenAsync(new(argument), DocumentAccess.Mapped, ct: ct);
            Console.WriteLine("ready");
            _ = await Console.In.ReadLineAsync(ct);
            Check(held.InspectWorkflow(static view => view.NodeCount) == 2, "Cross-process reader failed.");
        }
        else
        {
            Console.WriteLine((await child.PublishAsync(Fixture(argument), ct)).Id.Value);
        }
    }

    return;
}

DirectoryInfo temp = Directory.CreateTempSubdirectory("cmwf-doc-test-");
try
{
    string path = Path.Combine(temp.FullName, "primary");
    DocumentStore store = await DocumentStore.OpenStoreAsync(path, ct);
    DocumentDescriptor doc = await store.PublishRetainedAsync(Fixture(), "head", ct);
    Check(await store.ResolveReferenceAsync("head", ct) == doc, "Atomic root does not resolve.");
    Check(doc.Canonical.HasValue && doc.Blob.Value != doc.Canonical.Value.Value && doc.Id.Value != doc.Blob.Value,
        "Logical, canonical and physical identities collapsed.");
    DocumentDescriptor[] same = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.PublishAsync(Fixture(), ct)));
    Check(same.All(value => value == doc), "Concurrent identical publication differs.");
    Check((await store.CollectAsync(ct)).BlobsRemoved == 0, "Referenced document collected.");

    byte[] officialFrame;
    using (DocumentSession buffered = await store.OpenAsync(doc.Id, ct: ct))
    using (DocumentSession mapped = await store.OpenAsync(doc.Id, DocumentAccess.Mapped, ct: ct))
    {
        Check(buffered.Verification.Graph && mapped.Verification.Wire && mapped.Verification.CanonicalSegment, "Verification evidence absent.");
        officialFrame = mapped.ExportStoredBytes();
        Check(buffered.ExportStoredBytes().AsSpan().SequenceEqual(officialFrame), "Owners disagree on bytes.");
        Check(mapped.InspectWorkflow(static view => view.GetNode(1).RunSourceIndex) == 0 &&
            buffered.Materialize().Nodes[1].Arguments[1].Length == 0, "Borrowed inspection or DTO projection failed.");
        int[] counts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(
            () => mapped.InspectWorkflow(static view => view.NodeCount), ct)));
        Check(counts.All(count => count == 2), "Concurrent mapped inspections differ.");
    }

    if (args is ["--oracle", string oracle])
    {
        string input = Path.Combine(temp.FullName, "published.capnp");
        string output = Path.Combine(temp.FullName, "official.raw");
        await File.WriteAllBytesAsync(input, officialFrame, ct);
        using Process reference = Start(oracle, ["verify", input, output]);
        await reference.WaitForExitAsync(ct);
        Check(reference.ExitCode == 0 && Hash(await File.ReadAllBytesAsync(output, ct)) == doc.Canonical!.Value.Value,
            "Published frame failed official canonical comparison.");
        Console.WriteLine("Official C++ oracle accepted the stored frame.");
    }
    else if (args.Length != 0)
    {
        throw new ArgumentException("Use --oracle <official C++ oracle>.");
    }

    using (DocumentSession bytesOnly = await store.OpenAsync(doc.Id, verification: VerificationMode.StoredBytes, ct: ct))
    {
        Check(bytesOnly.Verification is { ExactBytes: true, Wire: false, Graph: false }, "Exact mode overclaimed evidence.");
    }

    string external = Path.Combine(temp.FullName, "legacy.json");
    byte[] json = [.. WorkflowDocuments.Project(Fixture()), 10];
    await File.WriteAllBytesAsync(external, json, ct);
    LegacyDigest legacy = new(Hash(json));
    await Fail<InvalidDataException>(() => store.ImportJsonAsync(json, new LegacyDigest(doc.Blob.Value), ct));
    MigrationResult rehearsal = await store.RehearseMigrationAsync(external, legacy, ct);
    Check(rehearsal.Binary == doc && rehearsal.Legacy.Legacy == legacy && rehearsal.Binary.Id != rehearsal.Legacy.Id
        && await store.FindEquivalenceAsync(rehearsal.Legacy.Id, doc.Id, ct) == rehearsal.NormalizedProjectionSha256
        && Hash(await File.ReadAllBytesAsync(external, ct)) == legacy.Value, "Migration equivalence or legacy byte preservation failed.");
    using (SqliteConnection tamper = new($"Data Source={Path.Combine(path, "documents.sqlite")};Pooling=false"))
    {
        await tamper.OpenAsync(ct);
        using SqliteCommand damage = tamper.CreateCommand();
        damage.CommandText = "UPDATE equivalence SET projection=replace(printf('%064d', 0), '0', 'x');";
        _ = await damage.ExecuteNonQueryAsync(ct);
    }

    await Fail<InvalidDataException>(() => store.FindEquivalenceAsync(rehearsal.Legacy.Id, doc.Id, ct));
    using (DocumentSession legacySession = await store.OpenAsync(rehearsal.Legacy.Id, ct: ct))
    {
        Check(legacySession.ExportStoredBytes().AsSpan().SequenceEqual(json) && legacySession.Materialize().Nodes.Count == 2,
            "Original JSON export changed bytes.");
    }

    WorkflowDocument old = Fixture();
    old.SchemaVersion = 1;
    byte[] oldJson = WorkflowDocuments.Project(old);
    _ = await store.ImportJsonAsync(oldJson, new(Hash(oldJson)), ct);
    await File.WriteAllBytesAsync(external, oldJson, ct);
    await Fail<InvalidDataException>(() => store.RehearseMigrationAsync(external, new LegacyDigest(Hash(oldJson)), ct));
    WorkflowDocument unsupported = Fixture();
    unsupported.Nodes.Add(new() { Id = "unsupported", Context = "local", Kind = "invoke", Target = "example" });
    byte[] unsupportedJson = WorkflowDocuments.Project(unsupported);
    _ = await store.ImportJsonAsync(unsupportedJson, new(Hash(unsupportedJson)), ct);
    await File.WriteAllBytesAsync(external, unsupportedJson, ct);
    await Fail<NotSupportedException>(() => store.RehearseMigrationAsync(external, new LegacyDigest(Hash(unsupportedJson)), ct));

    byte[] unknownJson = "{\"schemaVersion\":2,\"contexts\":{},\"nodes\":[],\"unrecognized\":true}"u8.ToArray();
    await Fail<System.Text.Json.JsonException>(() => store.ImportJsonAsync(unknownJson, new LegacyDigest(Hash(unknownJson)), ct));
    byte[] duplicateJson = "{\"schemaVersion\":1,\"schemaVersion\":2,\"contexts\":{},\"nodes\":[]}"u8.ToArray();
    await Fail<InvalidDataException>(() => store.ImportJsonAsync(duplicateJson, new LegacyDigest(Hash(duplicateJson)), ct));

    await Fail<ArgumentException>(() => store.FindAsync(default, ct));
    await Fail<ArgumentException>(() => store.PinAsync("bad\nname", doc.Id, ct));
    await Fail<InvalidDataException>(() => store.ImportBinaryAsync(officialFrame.AsSpan(0, officialFrame.Length - 1), ct));
    Check(await store.ImportBinaryAsync(officialFrame, ct) == doc, "Binary import differs.");
    byte[] extendedFrame = (byte[])officialFrame.Clone();
    // The canonical root's first data word contains the UInt32 schema version and four unused bytes.
    extendedFrame[20] = 42;
    DocumentDescriptor extended = await store.ImportBinaryAsync(extendedFrame, ct);
    using (DocumentSession evolved = await store.OpenAsync(extended.Id, ct: ct))
    using (DocumentSession original = await store.OpenAsync(doc.Id, ct: ct))
    {
        Check(extended.Id != doc.Id && evolved.InspectWire(static view => view.Root.AsStruct().ReadScalar(32, 32)) == 42
            && evolved.ExportStoredBytes().AsSpan().SequenceEqual(extendedFrame)
            && evolved.ExportJsonProjection().AsSpan().SequenceEqual(original.ExportJsonProjection()),
            "Unknown wire bytes were lost or entered the known JSON projection.");
    }

    Check(await store.RemoveReferenceAsync("head", ct), "Reference removal failed.");
    await store.PinAsync("keep", doc.Id, ct);
    Check(!await store.DeleteAsync(doc.Id, ct), "Explicit deletion removed a pin.");
    _ = await store.CollectAsync(ct);
    Check(await store.UnpinAsync("keep", ct), "Pin removal failed.");

    using (Process held = Child(path, "hold", doc.Id.Value))
    {
        Check(await held.StandardOutput.ReadLineAsync(ct) == "ready", "Child lease was not acquired.");
        Check(await store.DeleteAsync(doc.Id, ct), "Logical deletion of an unrooted row failed.");
        Check((await store.CollectAsync(ct)).ActiveBlobs == 1, "Collector removed a leased physical blob.");
        await held.StandardInput.WriteLineAsync("release");
        await held.WaitForExitAsync(ct);
        Check(held.ExitCode == 0, "Child lease inspection failed.");
        Check((await store.CollectAsync(ct)).BlobsRemoved == 1, "Released blob not collected.");
    }

    doc = await store.PublishAsync(Fixture(), ct);
    DocumentSession disposing = await store.OpenAsync(doc.Id, DocumentAccess.Mapped, ct: ct);
    TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using ManualResetEventSlim release = new();
    Task<int> reading = Task.Run(() => disposing.InspectWorkflow(view =>
    {
        entered.SetResult();
        release.Wait(ct);
        return view.NodeCount;
    }), ct);
    await entered.Task.WaitAsync(ct);
    try
    {
        disposing.Dispose();
        Check((await store.CollectAsync(ct)).ActiveBlobs == 1, "Disposal released active inspection lease.");
    }
    finally
    {
        release.Set();
    }

    Check(await reading == 2 && (await store.CollectAsync(ct)).BlobsRemoved == 1, "Deferred disposal failed.");

    using (Process publisher1 = Child(path, "publish", "same"))
    using (Process publisher2 = Child(path, "publish", "same"))
    using (Process publisher3 = Child(path, "publish", "different"))
    {
        string? first = await publisher1.StandardOutput.ReadLineAsync(ct);
        string? second = await publisher2.StandardOutput.ReadLineAsync(ct);
        string? third = await publisher3.StandardOutput.ReadLineAsync(ct);
        await Task.WhenAll(publisher1.WaitForExitAsync(ct), publisher2.WaitForExitAsync(ct), publisher3.WaitForExitAsync(ct));
        Check(publisher1.ExitCode == 0 && publisher2.ExitCode == 0 && publisher3.ExitCode == 0
            && first is not null && first == second && first != third, "Cross-process publication failed.");
    }

    doc = await store.PublishAsync(Fixture(), ct);
    string blob = Path.Combine(path, "payload", "blobs", doc.Blob.Value);
    byte[] corrupt = (byte[])officialFrame.Clone();
    corrupt[^1] ^= 1;
    await File.WriteAllBytesAsync(blob, corrupt, ct);
    await Fail<InvalidDataException>(() => store.OpenAsync(doc.Id, DocumentAccess.Mapped, ct: ct));
    await Fail<InvalidDataException>(() => store.PublishAsync(Fixture(), ct));
    await File.WriteAllBytesAsync(blob, officialFrame[..^1], ct);
    await Fail<InvalidDataException>(() => store.OpenAsync(doc.Id, ct: ct));
    File.Delete(blob);
    await Fail<FileNotFoundException>(() => store.OpenAsync(doc.Id, ct: ct));
    if (!OperatingSystem.IsWindows())
    {
        _ = File.CreateSymbolicLink(blob, external);
        await Fail<InvalidDataException>(() => store.OpenAsync(doc.Id, ct: ct));
        File.Delete(blob);
    }

    _ = await store.PublishAsync(Fixture(), ct);
    using (SqliteConnection tamper = new($"Data Source={Path.Combine(path, "documents.sqlite")};Pooling=false"))
    {
        await tamper.OpenAsync(ct);
        using SqliteCommand damage = tamper.CreateCommand();
        damage.CommandText = "UPDATE documents SET stored_length=stored_length+8;";
        _ = await damage.ExecuteNonQueryAsync(ct);
    }

    await Fail<InvalidDataException>(() => store.PublishAsync(Fixture(), ct));
    await Fail<InvalidDataException>(() => store.OpenAsync(doc.Id, ct: ct));
    await Fail<InvalidDataException>(() =>
    {
        _ = WorkflowDocuments.Verify(officialFrame, doc with { Canonical = new CanonicalDigest(new string('0', 64)) },
            VerificationMode.Full);
        return Task.CompletedTask;
    });
    await Fail<InvalidDataException>(() =>
    {
        _ = DocumentDescriptor.Create(DocumentCodec.WorkflowCapnp, 3, doc.Blob, doc.Canonical, doc.StoredLength);
        return Task.CompletedTask;
    });

    await ProductionArchiveCases.RunAsync(temp.FullName, args.Length == 2 ? args[1] : null, ct);
    await CheckPublicationRace(Path.Combine(temp.FullName, "publish-vs-gc"), ct);
    await CheckFaults(temp.FullName, doc.Id, ct);
    await CheckRetentionModel(Path.Combine(temp.FullName, "model"), ct);
    Console.WriteLine("Document identities, migration, cross-process leases, corruption, fault recovery and retention model passed.");
}
finally
{
    temp.Delete(recursive: true);
}

static async Task CheckPublicationRace(string path, CancellationToken ct)
{
    TaskCompletionSource exposedBlob = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using ManualResetEventSlim finishPublication = new();
    DocumentStore publisher = await DocumentStore.InitializeAsync(path, ct, point =>
    {
        if (point == StoreFault.AfterPublish)
        {
            exposedBlob.SetResult();
            finishPublication.Wait(ct);
        }
    });
    DocumentStore collector = await DocumentStore.OpenStoreAsync(path, ct);
    Task<DocumentDescriptor> publishing = Task.Run(() => publisher.PublishRetainedAsync(Fixture(), "new", ct), ct);
    await exposedBlob.Task.WaitAsync(ct);
    Task<CollectionResult> collecting;
    try
    {
        collecting = collector.CollectAsync(ct);
        Check(!collecting.IsCompleted, "Collection overtook a publication awaiting its catalog commit.");
    }
    finally
    {
        finishPublication.Set();
    }

    DocumentDescriptor descriptor = await publishing;
    Check((await collecting).BlobsRemoved == 0 && await collector.ResolveReferenceAsync("new", ct) == descriptor,
        "Collection pruned an actively published, retained document.");
}

static async Task CheckFaults(string root, DocumentId id, CancellationToken ct)
{
    StoreFault[] publication = [StoreFault.BeforeStage, StoreFault.DuringStage, StoreFault.AfterWrite,
        StoreFault.AfterFlush, StoreFault.BeforePublish, StoreFault.AfterPublish, StoreFault.BeforeCommit,
        StoreFault.AfterCommit, StoreFault.BeforeReturn];
    foreach (StoreFault at in publication)
    {
        string path = Path.Combine(root, "throw-" + at);
        DocumentStore failed = await DocumentStore.InitializeAsync(path, ct, point =>
        {
            if (point == at)
            {
                throw new Interrupted();
            }
        });
        await Fail<Interrupted>(() => failed.PublishAsync(Fixture(), ct));
        DocumentStore reopened = await DocumentStore.OpenStoreAsync(path, ct);
        Check((await reopened.FindAsync(id, ct) is not null) == (at is StoreFault.AfterCommit or StoreFault.BeforeReturn),
            "Publication metadata visible on wrong side of commit.");
        Check((await reopened.PublishAsync(Fixture(), ct)).Id == id, "Publication retry changed identity.");
    }

    foreach (StoreFault at in new[] { StoreFault.DuringStage, StoreFault.AfterPublish, StoreFault.AfterCommit })
    {
        string path = Path.Combine(root, "kill-" + at);
        using Process process = Child(path, "crash", at.ToString());
        Check(await process.StandardOutput.ReadLineAsync(ct) == "ready", "Child did not reach crash barrier.");
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(ct);
        DocumentStore reopened = await DocumentStore.OpenStoreAsync(path, ct);
        bool committed = at == StoreFault.AfterCommit;
        Check((await reopened.FindAsync(id, ct) is not null) == committed, "Wrong state after process kill.");
        if (!committed)
        {
            CollectionResult recovered = await reopened.CollectAsync(ct);
            Check(at == StoreFault.DuringStage ? recovered.StagingFilesRemoved == 1 : recovered.BlobsRemoved == 1,
                "Crash orphan was not recovered.");
        }

        Check((await reopened.PublishAsync(Fixture(), ct)).Id == id, "Crash retry changed identity.");
    }

    string gcPath = Path.Combine(root, "gc-rollback");
    DocumentStore gc = await DocumentStore.InitializeAsync(gcPath, ct, point =>
    {
        if (point is StoreFault.BeforeReferenceCommit or StoreFault.AfterPrune)
        {
            throw new Interrupted();
        }
    });
    _ = await gc.PublishRetainedAsync(Fixture(), "keep", ct);
    await Fail<Interrupted>(() => gc.RemoveReferenceAsync("keep", ct));
    Check(await gc.ResolveReferenceAsync("keep", ct) is not null, "Reference transaction did not roll back.");
    DocumentStore normal = await DocumentStore.OpenStoreAsync(gcPath, ct);
    Check(await normal.RemoveReferenceAsync("keep", ct), "Can’t release reference after failure.");
    await Fail<Interrupted>(() => gc.CollectAsync(ct));
    Check(await normal.FindAsync(id, ct) is null && (await normal.CollectAsync(ct)).BlobsRemoved == 1,
        "GC retry did not collect post-prune orphan.");
}

static async Task CheckRetentionModel(string path, CancellationToken ct)
{
    DocumentStore store = await DocumentStore.OpenStoreAsync(path, ct);
    bool[] exists = new bool[4], pinned = new bool[4];
    DocumentId[] ids = new DocumentId[4];
    for (int step = 0; step < 96; step++)
    {
        byte[] sample = SHA256.HashData(BitConverter.GetBytes(step + 8179));
        int i = sample[0] % 4;
        string name = "entry" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (sample[1] % 4)
        {
            case 0:
                ids[i] = (await store.PublishAsync(Fixture(name), ct)).Id;
                exists[i] = true;
                break;
            case 1 when exists[i]:
                await store.PinAsync(name, ids[i], ct);
                pinned[i] = true;
                break;
            case 2:
                _ = await store.UnpinAsync(name, ct);
                pinned[i] = false;
                break;
            case 3:
                _ = await store.CollectAsync(ct);
                for (int j = 0; j < ids.Length; j++)
                {
                    exists[j] &= pinned[j];
                }

                break;
            default:
                break;
        }

        for (int j = 0; j < ids.Length; j++)
        {
            if (ids[j] != default)
            {
                Check((await store.FindAsync(ids[j], ct) is not null) == exists[j], "Retention model diverged.");
            }
        }
    }
}

static Process Child(string path, string operation, string value) =>
    Start("dotnet", [Assembly.GetExecutingAssembly().Location, "child", path, operation, value]);

static Process Start(string executable, string[] args)
{
    ProcessStartInfo start = new(executable) { RedirectStandardInput = true, RedirectStandardOutput = true };
    foreach (string arg in args)
    {
        start.ArgumentList.Add(arg);
    }

    return Process.Start(start) ?? throw new InvalidOperationException("Could not start child.");
}

static WorkflowDocument Fixture(string buildDirectory = "build") => new()
{
    SchemaVersion = 2,
    Contexts = { ["local"] = new() { BuildDirectory = buildDirectory } },
    Nodes =
    [
        new() { Id = "build", Context = "local", Kind = "build", Target = "example" },
        new() { Id = "run", Kind = "run", From = "build", DependsOn = ["build"], Arguments = ["--test", ""] }
    ]
};

static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

static async Task Fail<T>(Func<Task> action) where T : Exception
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

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class Interrupted : Exception
{
    internal Interrupted()
    {
    }

    internal Interrupted(string message) : base(message)
    {
    }

    internal Interrupted(string message, Exception innerException) : base(message, innerException)
    {
    }
}
