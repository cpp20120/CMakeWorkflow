using System.Buffers.Binary;
using System.Text.Json;
using System.Security.Cryptography;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Contracts.Workflows;

const string fixtureHash = "a855f400b61bcf99bf4e8a51926ef026702b2eddd66a49c04acf585c6bb71feb";
WorkflowDocument fixture = Fixture();
byte[] staged = WorkflowEncoding.Encode(fixture);
byte[] canonical = CapnpWire.Canonicalize(staged);
Check(!staged.AsSpan().SequenceEqual(canonical), "Expected noncanonical staging layout.");
Check(CapnpWire.HashCanonical(canonical) == fixtureHash, "Official workflow digest changed.");
Equal(canonical, CapnpWire.Canonicalize(canonical), "Canonicalization is idempotent.");
Check(JsonSerializer.Serialize(fixture, WorkflowDefinitionJson.Default.WorkflowDocument)
      == JsonSerializer.Serialize(WorkflowEncoding.Decode(canonical), WorkflowDefinitionJson.Default.WorkflowDocument),
    "Round trip lost supported domain fields.");
Inspect(canonical);

WorkflowDocument minimal = new()
{
    SchemaVersion = 2,
    Nodes = [new() { Id = "build", Kind = "build", Target = "app" }]
};
byte[] golden = Convert.FromHexString(
    "0000000001000200020000000000000000000000000000000100000067000000"
    + "0400000001000b00000001000000000029000000320000000000000000000000"
    + "0000000000000000000000000000000000000000000000000000000000000000"
    + "0000000000000000000000000000000000000000000000000000000000000000"
    + "05000000220000006275696c640000006170700000000000");
Equal(golden, CapnpWire.Canonicalize(WorkflowEncoding.Encode(minimal)), "Official minimal workflow golden bytes.");
Check(CapnpWire.HashCanonical(golden) == "22ebbe594ef86872447693205ca0422da0e768fc2dd076fbf9f49c646731fc6d",
    "Minimal workflow digest excludes the segment table.");

byte[] frame = CapnpWire.Frame(canonical);
Equal(canonical, CapnpWire.Unframe(frame), "Framing changes segment bytes.");
byte[] backwards = Words(0x0001000000000008, 7, 99, 0x00000001fffffff4);
Equal(Words(0x0001000000000000, 0x0000000100000000, 7), CapnpWire.Canonicalize(backwards),
    "Backward near pointer and non-preorder layout.");
Equal(Words(0x0000000100000000, 7),
    CapnpWire.Canonicalize(Words(0x0000000200000000, 7, 0)), "Trailing data word removal.");
Equal(Words(0x00000000fffffffc),
    CapnpWire.Canonicalize(Words(0x0000000100000000, 0)), "Zero-sized struct offset.");
Equal(Words(0x0000000100000000, 7), CapnpWire.Canonicalize(Words(0x0000000100000000, 7, 99)),
    "Unreachable words are excluded from identity.");

for (int i = 0; i < 12; i++)
{
    WorkflowDocument equivalent = Fixture();
    WorkflowNode run = equivalent.Nodes[2];
    if ((i & 1) != 0)
    {
        equivalent.Contexts = equivalent.Contexts.Reverse()
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    if ((i & 2) != 0)
    {
        run.Environment = run.Environment.Reverse()
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    run.DependsOn.Insert(i >> 2, "build");
    byte[] actual = CapnpWire.Canonicalize(WorkflowEncoding.Encode(equivalent));
    Equal(canonical, actual, "Map and explicit-edge construction order affected the canonical segment.");
    Check(CapnpWire.HashCanonical(actual) == fixtureHash, "Equivalent workflow changed identity.");
}

WorkflowDocument reordered = Fixture();
reordered.Nodes.Reverse();
Check(CapnpWire.HashCanonical(CapnpWire.Canonicalize(WorkflowEncoding.Encode(reordered))) != fixtureHash,
    "The planner-relevant node order must affect identity.");

DirectoryInfo directory = Directory.CreateTempSubdirectory("cmwf-capnp-tests-");
try
{
    string path = Path.Combine(directory.FullName, "fixture.capnp");
    await File.WriteAllBytesAsync(path, frame);
    Check(MappedWorkflow.Read(path, static view => view.GetNode(3).GetDependencyIndex(1) == 2
          && view.GetNode(1).BuildTargetUtf8.SequenceEqual("tool"u8)), "Mapped access failed.");
    Reject(() => MappedWorkflow.Read<bool>(path, static _ => throw new InvalidDataException("callback")),
        "Mapped callback failure is propagated.");
    File.Delete(path);
    await File.WriteAllBytesAsync(path, frame[..^1]);
    Reject(() => MappedWorkflow.Read(path, static _ => true), "Mapped file truncation.");

    if (args is [string cli, string oracle])
    {
        await OfficialInterop.CheckAsync(cli, oracle, directory.FullName, fixture, canonical);
        Console.WriteLine("Official CLI and C++ canonicalization, interoperability and schema evolution passed.");
    }
    else if (args.Length != 0)
    {
        throw new ArgumentException("Supply the paths to capnp and cmwf-capnp-oracle together.");
    }
    else
    {
        Console.WriteLine("Live official-tool checks skipped. Supply capnp and cmwf-capnp-oracle to run them.");
    }
}
finally
{
    directory.Delete(recursive: true);
}

for (int length = 0; length < canonical.Length; length++)
{
    int cutoff = length;
    Reject(() => WorkflowEncoding.Decode(canonical.AsSpan(0, cutoff)), "Truncated segment accepted.");
}

Reject(() => CapnpWire.Unframe(Words(0xffffffff00000000, 0)), "Overflowing frame length.");
Reject(() => CapnpWire.Validate(Words(0x000100007ffffffc)), "Large positive pointer offset.");
Reject(() => CapnpWire.Validate(Words(0x0001000080000000)), "Large negative pointer offset.");
Reject(() => CapnpWire.Validate(Words(2)), "Far pointer.");
Reject(() => CapnpWire.Validate(Words(3)), "Capability pointer.");
Reject(() => CapnpWire.Validate(Words(0x0001000000000000, 0x00010000fffffffc)), "Pointer cycle.");
Reject(() => CapnpWire.Validate(Words(0xffffffff00000001)), "Overflowing composite word count.");
Reject(() => CapnpWire.Validate(Words(0x0000000f00000001, 0xfffffffffffffffc, 0)), "Overflowing composite element product.");
Reject(() => CapnpWire.Validate(Words(0x0000000f00000001, 1, 0)), "Non-struct composite tag.");
Reject(() => CapnpWire.Validate(Words(0x0000000700000001, 0x00000000fffffffc)), "Zero-stride list traversal amplification.");
Reject(() => CapnpWire.Validate(Words(0xfffffff800000001)), "Void-list traversal amplification.");

ulong[] deep = new ulong[35];
Array.Fill(deep, 0x0001000000000000UL);
deep[^1] = 0;
Reject(() => CapnpWire.Validate(Words(deep)), "Deep acyclic nesting limit.");

byte[] invalidKind = (byte[])canonical.Clone();
int nodeList = Target(invalidKind, PointerSection(invalidKind, 0) + 1);
int firstNode = nodeList + 1;
invalidKind[(firstNode * 8) + 2] = 255;
Reject(() => WorkflowEncoding.Decode(invalidKind), "Unknown operation discriminant.");
byte[] invalidText = (byte[])canonical.Clone();
int firstId = Target(invalidText, firstNode + (ushort)(ReadWord(invalidText, nodeList) >> 32));
invalidText[firstId * 8] = 255;
Reject(() => WorkflowEncoding.Decode(invalidText), "Invalid UTF-8.");
byte[] unterminated = (byte[])canonical.Clone();
unterminated[(firstId * 8) + "generate".Length] = 1;
Reject(() => WorkflowEncoding.Decode(unterminated), "Unterminated text.");
byte[] invalidDependency = (byte[])canonical.Clone();
ulong nodeTag = ReadWord(invalidDependency, nodeList);
int nodeStride = (ushort)(nodeTag >> 32) + (ushort)(nodeTag >> 48);
int buildDependencies = Target(invalidDependency, firstNode + nodeStride + (ushort)(nodeTag >> 32) + 2);
BinaryPrimitives.WriteUInt32LittleEndian(invalidDependency.AsSpan(buildDependencies * 8), uint.MaxValue);
Reject(() => WorkflowEncoding.Decode(invalidDependency), "Invalid dependency index.");

for (int i = 0; i < 10_000; i++)
{
    byte[] sample = Sample(i);
    byte[] corrupted = i % 2 == 0 ? (byte[])canonical.Clone() : new byte[(1 + (sample[0] % 64)) * 8];
    if (i % 2 == 0)
    {
        for (int change = 0; change < 1 + (i % 7); change++)
        {
            int offset = change * 3;
            int position = BinaryPrimitives.ReadUInt16LittleEndian(sample.AsSpan(offset)) % corrupted.Length;
            corrupted[position] ^= sample[offset + 2];
        }
    }
    else
    {
        for (int offset = 0; offset < corrupted.Length; offset += sample.Length)
        {
            byte[] block = Sample((i * 512) + offset);
            block.AsSpan(0, Math.Min(block.Length, corrupted.Length - offset)).CopyTo(corrupted.AsSpan(offset));
        }
    }

    try
    {
        byte[] normalized = CapnpWire.Canonicalize(corrupted);
        Equal(normalized, CapnpWire.Canonicalize(normalized), "Accepted mutation canonicalized inconsistently.");
        _ = WorkflowEncoding.Decode(corrupted);
    }
    catch (InvalidDataException)
    {
        // Expected refusal for malformed or out-of-subset input.
    }
}

Console.WriteLine("Workflow codec, canonical vectors, mapped input and 10,000 seeded adversarial cases passed.");

static WorkflowDocument Fixture() => new()
{
    SchemaVersion = 2,
    Contexts =
    {
        ["debug"] = new()
        {
            BuildDirectory = "/build/debug", Configuration = "Debug", ConfigurePreset = "debug",
            SourceDirectory = "/src", Mode = "managed", ProvenancePolicy = "strict"
        },
        ["release"] = new()
        {
            BuildDirectory = "/build/release", Configuration = "Release", Mode = "adopt",
            ProvenancePolicy = "allowUnverified", CompilerCacheProfile = "local-cache"
        }
    },
    Nodes =
    [
        new() { Id = "generate", Kind = "configure", Context = "debug" },
        new()
        {
            Id = "build", Kind = "build", Context = "debug", Target = "tool", DependsOn = ["generate"],
            Outputs = [new() { Name = "tool", Path = "bin/tool", Fresh = true }]
        },
        new()
        {
            Id = "run", Kind = "run", From = "build", DependsOn = ["generate", "build"],
            Arguments = ["--flag", "", "μ"], WorkingDirectory = ".",
            Environment = { ["LANG"] = "C", ["MODE"] = "test" },
            Resources = ["slot"], Exclusive = true, TimeoutSeconds = 4.5,
            Inputs = [new() { From = "build", Artifact = "tool", Snapshot = true }],
            Constraints = new() { CpuSet = "2" }
        },
        new() { Id = "verify", Kind = "ctest", Context = "debug", DependsOn = ["build", "run"], LabelRegex = "^unit$" }
    ]
};

static void Inspect(ReadOnlySpan<byte> bytes)
{
    WorkflowView view = new(bytes);
    NodeView node = view.GetNode(2);
    Check(view.NodeCount == 4 && node.IdUtf8.SequenceEqual("run"u8) && node.Kind == OperationKind.Run
          && node.DependencyCount == 2 && node.GetDependencyIndex(1) == 1 && node.RunSourceIndex == 1
          && node.InputCount == 1 && node.GetInputProducerIndex(0) == 1, "Direct read and edge traversal.");
}

static void Check(bool check, string message)
{
    if (!check)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string message) =>
    Check(expected.SequenceEqual(actual), message);

static void Reject(Action operation, string reason)
{
    try
    {
        operation();
    }
    catch (InvalidDataException)
    {
        return;
    }

    throw new InvalidOperationException(reason);
}

static byte[] Sample(int seed)
{
    Span<byte> input = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(input, seed);
    return SHA256.HashData(input);
}

static byte[] Words(params ulong[] words)
{
    byte[] bytes = new byte[words.Length * 8];
    for (int i = 0; i < words.Length; i++)
    {
        WriteWord(bytes, i, words[i]);
    }

    return bytes;
}

static ulong ReadWord(byte[] segment, int word) => BinaryPrimitives.ReadUInt64LittleEndian(segment.AsSpan(word * 8));
static void WriteWord(byte[] segment, int word, ulong value) =>
    BinaryPrimitives.WriteUInt64LittleEndian(segment.AsSpan(word * 8), value);

static int Target(byte[] segment, int pointer) =>
    pointer + 1 + (unchecked((int)ReadWord(segment, pointer)) >> 2);

static int PointerSection(byte[] segment, int tag) => Target(segment, tag) + (ushort)(ReadWord(segment, tag) >> 32);
