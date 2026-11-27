using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Contracts.Workflows;

WorkflowDocument document = new()
{
    SchemaVersion = 2,
    Contexts = { ["release"] = new() { BuildDirectory = "/build/release", Configuration = "Release" } },
    Nodes =
    [
        new() { Id = "build", Kind = "build", Context = "release", Target = "demo" },
        new() { Id = "run", Kind = "run", From = "build", DependsOn = ["build"], Arguments = ["--verify"] }
    ]
};

byte[] staged = WorkflowEncoding.Encode(document);
byte[] canonical = CapnpWire.Canonicalize(staged);
string hash = CapnpWire.HashCanonical(canonical);
WorkflowDocument decoded = WorkflowEncoding.Decode(canonical);
WorkflowView view = new(canonical);
if (decoded.Nodes[0].Target != "demo" || view.GetNode(1).GetDependencyIndex(0) != 0
    || !view.GetNode(0).BuildTargetUtf8.SequenceEqual("demo"u8)
    || !canonical.AsSpan().SequenceEqual(CapnpWire.Canonicalize(WorkflowEncoding.Encode(decoded))))
{
    throw new InvalidDataException("NativeAOT workflow round trip failed.");
}

DirectoryInfo directory = Directory.CreateTempSubdirectory("cmwf-capnp-aot-");
try
{
    string path = Path.Combine(directory.FullName, "workflow.capnp");
    byte[] frame = CapnpWire.Frame(canonical);
    File.WriteAllBytes(path, frame);
    if (!MappedWorkflow.Read(path, static mapped => mapped.GetNode(1).IdUtf8.SequenceEqual("run"u8)
                            && mapped.GetNode(1).RunSourceIndex == 0))
    {
        throw new InvalidDataException("NativeAOT file-backed read failed.");
    }

    Console.WriteLine($"Cap’n Proto NativeAOT smoke passed. bytes={frame.Length} sha256={hash}");
}
finally
{
    directory.Delete(recursive: true);
}
