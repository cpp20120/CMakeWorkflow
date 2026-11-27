using System.Security.Cryptography;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Storage.Documents;

DirectoryInfo temp = Directory.CreateTempSubdirectory("cmwf-document-aot-");
try
{
    string root = Path.Combine(temp.FullName, "documents");
    DocumentStore store = await DocumentStore.OpenStoreAsync(root);
    WorkflowDocument workflow = new()
    {
        SchemaVersion = 2,
        Contexts = { ["native"] = new() { BuildDirectory = "build" } },
        Nodes =
        [
            new() { Id = "compile", Kind = "build", Context = "native", Target = "tool" },
            new() { Id = "run", Kind = "run", From = "compile", DependsOn = ["compile"] }
        ]
    };
    DocumentDescriptor descriptor = await store.PublishRetainedAsync(workflow, "head");
    byte[] json;
    using (DocumentSession mapped = await store.OpenAsync(descriptor.Id, DocumentAccess.Mapped))
    using (DocumentSession buffered = await store.OpenAsync(descriptor.Id))
    {
        if (!mapped.Verification.Graph || !buffered.Verification.CanonicalSegment
            || !mapped.InspectWorkflow(static view => view.GetNodeIdUtf8(0).SequenceEqual("compile"u8))
            || buffered.Materialize().Nodes[1].From != "compile")
        {
            throw new InvalidDataException("NativeAOT document verification or inspection failed.");
        }

        json = buffered.ExportJsonProjection();
    }

    string external = Path.Combine(temp.FullName, "external.json");
    await File.WriteAllBytesAsync(external, json);
    MigrationResult migration = await store.RehearseMigrationAsync(external,
        new(Convert.ToHexStringLower(SHA256.HashData(json))));
    DocumentStore reopened = await DocumentStore.OpenStoreAsync(root);
    if (migration.Binary != descriptor || await reopened.ResolveReferenceAsync("head") != descriptor
        || await reopened.FindEquivalenceAsync(migration.Legacy.Id, descriptor.Id) != migration.NormalizedProjectionSha256
        || !await reopened.RemoveReferenceAsync("head") || (await reopened.CollectAsync()).BlobsRemoved != 2)
    {
        throw new InvalidDataException("NativeAOT catalog reopen, migration or collection failed.");
    }

    Console.WriteLine($"Document store NativeAOT passed. logical={descriptor.Id} blob={descriptor.Blob} canonical={descriptor.Canonical}");
}
finally
{
    temp.Delete(recursive: true);
}
