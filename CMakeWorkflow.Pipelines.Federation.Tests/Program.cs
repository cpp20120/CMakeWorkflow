using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Pipelines;
using CMakeWorkflow.Pipelines.Artifacts;
using CMakeWorkflow.Pipelines.Federation;
using CMakeWorkflow.Pipelines.Persistence;

string directory = Path.Combine(Path.GetTempPath(), "cmwf-p5c1-" + Guid.NewGuid().ToString("N"));
_ = Directory.CreateDirectory(directory);
try
{
    await HostCatalogAndFetchAsync(directory);
    await RefuseUnboundHostAsync(directory);
    await P5c2AcceptanceTests.RunAsync();
}
finally
{
    Directory.Delete(directory, recursive: true);
}
Console.WriteLine("P5c1 federation: Host local/SSH binding, discovery, fetch, reverify, tamper and cross-operation refusal passed.");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new UnreachableException(message);
    }
}

static async Task RefusedAsync<T>(Func<Task> work) where T : Exception
{
    try
    {
        await work();
    }
    catch (T)
    {
        return;
    }
    throw new UnreachableException("Expected " + typeof(T).Name);
}

static PipelineRunSnapshot Prepare(PipelineLedger ledger)
{
    PipelineRunSnapshot pending = ledger.Accept("request-one", new PipelineDocument
    {
        SchemaVersion = 1,
        Name = "federated",
        Stages = [new PipelineStage
        {
            Id = "compile", Kind = "workflow",
            Workflow = new PipelineWorkflowStage { Manifest = "samples/build.json" }
        }]
    });
    PipelineRunSnapshot reserved = ledger.ReserveWorkflow(pending.OperationId, "compile", pending.Revision);
    PipelineRunSnapshot attached = ledger.BindChild(pending.OperationId, "compile", reserved.Revision, "child-1");
    PipelineRunSnapshot terminal = ledger.RecordOutcome(pending.OperationId, "compile", attached.Revision,
        true, "report:sha256:" + new string('F', 64));
    return terminal;
}

static async Task HostCatalogAndFetchAsync(string root)
{
    using PipelineLedger ledger = new(Path.Combine(root, "valid.sqlite"));
    PipelineRunSnapshot pipeline = Prepare(ledger);
    byte[] data = Encoding.UTF8.GetBytes("verified host artifact");
    FakeHost host = new(ArtifactOrigin.HostSsh, "ssh-host-A/workspace-1", "child-1", data);
    FederatedArtifactCoordinator coordinator = new(ledger);
    IReadOnlyList<FederatedArtifact> found = await coordinator.ListAsync(pipeline.OperationId, "compile", host);
    Check(found.Count == 1 && found[0].Key.Origin == ArtifactOrigin.HostSsh &&
        found[0].ExecutionBinding == ArtifactExecutionBinding.ExactOperation &&
        found[0].Key.SourceIdentity.Contains("host:", StringComparison.Ordinal),
        "Host binding lost provenance.");
    FederatedArtifact selected = await coordinator.InspectAsync(found[0].Key, host);
    Check(selected == found[0], "Inspect mutated host identity.");
    FederatedArtifactFetchReceipt receipt = await coordinator.FetchAsync(
        selected, Path.Combine(root, "fetched.bin"), host);
    Check(!receipt.IsDirectory && await FederatedArtifactCoordinator.VerifyAsync(receipt) &&
        host.Downloads == 1, "Host fetched bytes were not verified.");
    await File.WriteAllTextAsync(receipt.Path, "tampered");
    Check(!await FederatedArtifactCoordinator.VerifyAsync(receipt),
        "Tampered bytes passed post-download integrity verification.");

    FakeHost another = new(ArtifactOrigin.HostSsh, "ssh-host-B/workspace-1", "child-1", data);
    await RefusedAsync<KeyNotFoundException>(async () =>
        { _ = await coordinator.InspectAsync(found[0].Key, another); });
    FederatedArtifact invalidDigest = found[0] with { Sha256 = new string('0', 64) };
    await RefusedAsync<InvalidDataException>(async () =>
        { _ = await coordinator.FetchAsync(invalidDigest, Path.Combine(root, "wrong.bin"), host); });
    Check(another.Downloads == 0 && host.Downloads == 1,
        "A rejected operation had download side effects.");
}

static async Task RefuseUnboundHostAsync(string root)
{
    using PipelineLedger ledger = new(Path.Combine(root, "rejected.sqlite"));
    PipelineRunSnapshot pipeline = Prepare(ledger);
    FederatedArtifactCoordinator coordinator = new(ledger);
    FakeHost wrong = new(ArtifactOrigin.HostLocal, "localhost/workspace-1", "child-other",
        Encoding.UTF8.GetBytes("data"));
    await RefusedAsync<InvalidOperationException>(async () =>
        { _ = await coordinator.ListAsync(pipeline.OperationId, "compile", wrong); });
    await RefusedAsync<InvalidOperationException>(async () =>
        { _ = await coordinator.ListAsync(pipeline.OperationId, "compile"); });
    Check(wrong.Lists == 0, "Mismatched child reached Host RPC.");
}

file sealed class FakeHost(ArtifactOrigin origin, string identity, string operationId, byte[] bytes)
    : IWorkflowHostArtifactSource
{
    public ArtifactOrigin Origin { get; } = origin;
    public string HostIdentity { get; } = identity;
    public string OperationId { get; } = operationId;
    public int Lists { get; private set; }
    public int Downloads { get; private set; }

    public Task<OperationArtifactList> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lists++;
        return Task.FromResult(new OperationArtifactList(OperationId, new string('A', 64),
        [new OperationArtifactInfo("log", "log", null, "logs/stdout.log", bytes.Length,
            638900000000000000, Convert.ToHexString(SHA256.HashData(bytes)))]));
    }

    public async Task<string> DownloadAsync(OperationArtifactInfo artifact, string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Downloads++;
        _ = artifact;
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
        return Path.GetFullPath(destinationPath);
    }
}
