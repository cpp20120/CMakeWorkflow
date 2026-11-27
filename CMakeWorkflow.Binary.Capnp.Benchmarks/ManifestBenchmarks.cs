using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Binary.Capnp.Benchmarks;

[MemoryDiagnoser]
[SuppressMessage("Maintainability", "CA1515", Justification =
    "BenchmarkDotNet generates and compiles a separate benchmark host that derives from this class.")]
public class ManifestBenchmarks
{
    private static readonly WorkflowInspection<int> inspect = view => view.GetNodeIdUtf8(view.NodeCount / 2).Length;
    private WorkflowDocument document = null!;
    private WorkflowDocument parsedJson = null!;
    private byte[] json = null!;
    private byte[] segment = null!;
    private byte[] canonical = null!;
    private DirectoryInfo directory = null!;
    private string jsonFile = null!;
    private string capnpFile = null!;

    [Params(4, 65, 256, 512)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        document = MatrixDocuments.Create(Size);
        json = JsonEncode();
        parsedJson = JsonDecode();
        segment = BinaryEncode();
        canonical = BinaryCanonicalize();
        byte[] frame = CapnpWire.Frame(canonical);
        if (BinaryDecode().Nodes.Count != parsedJson.Nodes.Count || JsonTraverse() != BinaryTraverse()
            || JsonLookup() != BinaryLookup())
        {
            throw new InvalidDataException("The benchmark inputs are not equivalent.");
        }

        directory = Directory.CreateTempSubdirectory("cmwf-capnp-bdn-");
        jsonFile = Path.Combine(directory.FullName, "workflow.json");
        capnpFile = Path.Combine(directory.FullName, "workflow.capnp");
        File.WriteAllBytes(jsonFile, json);
        File.WriteAllBytes(capnpFile, frame);
        Console.WriteLine($"nodes={Size} json={json.Length} staging={segment.Length} canonical={canonical.Length} "
                          + $"framed={frame.Length}");
    }

    [GlobalCleanup]
    public void Cleanup() => directory.Delete(recursive: true);

    [Benchmark]
    public byte[] JsonEncode() => WorkflowManifestBytes.Serialize(document);

    [Benchmark]
    public byte[] BinaryEncode() => WorkflowEncoding.Encode(document);

    [Benchmark]
    public byte[] BinaryCanonicalize() => CapnpWire.Canonicalize(segment);

    [Benchmark]
    public WorkflowDocument JsonDecode() => ReadJson(json);

    [Benchmark]
    public WorkflowDocument BinaryDecode() => WorkflowEncoding.Decode(canonical);

    [Benchmark]
    public int JsonDecodeAndLookup() => ReadJson(json).Nodes[Size / 2].Id.Length;

    [Benchmark]
    public int JsonLookup() => parsedJson.Nodes[Size / 2].Id.Length;

    [Benchmark]
    public int BinaryLookup() => inspect(new WorkflowView(canonical));

    [Benchmark]
    public int JsonTraverse() => MatrixDocuments.Traverse(parsedJson);

    [Benchmark]
    public int BinaryTraverse() => MatrixDocuments.Traverse(new WorkflowView(canonical));

    [Benchmark]
    public string JsonHash() => Convert.ToHexStringLower(SHA256.HashData(json));

    [Benchmark]
    public string BinaryHash() => CapnpWire.HashCanonical(canonical);

    [Benchmark]
    public int JsonFileLookup() => ReadJson(File.ReadAllBytes(jsonFile)).Nodes[Size / 2].Id.Length;

    [Benchmark]
    public int BinaryMappedLookup() => MappedWorkflow.Read(capnpFile, inspect);

    internal static WorkflowDocument ReadJson(byte[] bytes) =>
        JsonSerializer.Deserialize(bytes, WorkflowDefinitionJson.Default.WorkflowDocument)
        ?? throw new InvalidDataException("Empty workflow document.");
}
