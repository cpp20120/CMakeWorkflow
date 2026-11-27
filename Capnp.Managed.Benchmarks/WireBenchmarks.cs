using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;

namespace Capnp.Managed.Benchmarks;

[MemoryDiagnoser]
[SuppressMessage("Maintainability", "CA1515", Justification =
    "BenchmarkDotNet generates a separate benchmark host that derives from this class.")]
public class WireBenchmarks
{
    private static readonly MessageInspection<ulong> inspectScalar = message => message.Root.AsStruct().ReadScalar(0, 64);
    private int graphNodes;
    private byte[] near = null!;
    private byte[] graph = null!;
    private byte[] framed = null!;
    private byte[] multiFramed = null!;
    private ReadOnlyMemory<byte>[] far = null!;
    private ReadOnlyMemory<byte>[] doubleFar = null!;
    private OwnedMessage owned = null!;
    private DirectoryInfo directory = null!;
    private string path = null!;
    private MappedMessage mapped = null!;

    [GlobalSetup]
    public void Setup()
    {
        near = Words(0x0000000100000000, 42);
        far = [Words(0x0000000100000002), near];
        doubleFar = [Words(0x0000000100000006), Words(0x0000000200000002, 0x0000000100000000), Words(42)];
        multiFramed = FrameCodec.Write(new MessageReader(doubleFar));
        graphNodes = 512;
        graph = Build(graphNodes);
        framed = FrameCodec.Write(new MessageReader(graph));
        owned = OwnedMessage.CopyFrame(framed);
        directory = Directory.CreateTempSubdirectory("capnp-managed-bench-");
        path = Path.Combine(directory.FullName, "fixture.bin");
        File.WriteAllBytes(path, framed);
        mapped = MappedMessage.Open(path);
        if (Near() != 42 || Far() != 42 || DoubleFar() != 42 || ReadMultiSegmentFrame() != 42
            || Primitive() != 42 || IndexedList() != 511 || TextSpan() != 7 || Traverse() != 130816)
        {
            throw new InvalidDataException("Runtime benchmark fixtures differ.");
        }

        Console.WriteLine($"graph={graph.Length} framed={framed.Length} near={near.Length} "
                          + $"far-frame={FrameCodec.Write(new MessageReader(far)).Length} double-frame={multiFramed.Length}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        mapped.Dispose();
        directory.Delete(recursive: true);
    }

    [Benchmark]
    public ulong Near() => inspectScalar(new MessageReader(near));

    [Benchmark]
    public ulong Far() => inspectScalar(new MessageReader(far));

    [Benchmark]
    public ulong DoubleFar() => inspectScalar(new MessageReader(doubleFar));

    [Benchmark]
    public ulong ReadMultiSegmentFrame() => inspectScalar(FrameCodec.Read(multiFramed));

    [Benchmark]
    public ulong Primitive() => new MessageReader(graph).Root.AsStruct().ReadScalar(0, 64);

    [Benchmark]
    public ulong IndexedList() => new MessageReader(graph).Root.AsStruct().GetPointer(0)
        .AsList(ListElementSize.Composite).GetStruct(511).ReadScalar(0, 32);

    [Benchmark]
    public int TextSpan() => new MessageReader(graph).Root.AsStruct().GetPointer(1).AsText().Length;

    [Benchmark]
    public ulong Traverse()
    {
        ListReader list = new MessageReader(graph).Root.AsStruct().GetPointer(0).AsList(ListElementSize.Composite);
        ulong sum = 0;
        for (int i = 0; i < list.Count; i++)
        {
            sum += list.GetStruct(i).ReadScalar(0, 32);
        }

        return sum;
    }

    [Benchmark]
    public void Validate() => CanonicalMessage.Validate(new MessageReader(graph));

    [Benchmark]
    public byte[] BuildAndGrow() => Build(graphNodes);

    [Benchmark]
    public byte[] Canonicalize() => CanonicalMessage.CreateSegment(new MessageReader(graph));

    [Benchmark]
    public byte[] Frame() => FrameCodec.Write(new MessageReader(graph));

    [Benchmark]
    public ulong ReadOwned() => owned.Inspect(inspectScalar);

    [Benchmark]
    public ulong ReadMapped() => mapped.Inspect(inspectScalar);

    [Benchmark]
    public ulong OpenMapped()
    {
        using MappedMessage message = MappedMessage.Open(path);
        return message.Inspect(inspectScalar);
    }

    private static byte[] Build(int nodeCount)
    {
        MessageBuilder builder = new();
        StructBuilder root = builder.InitRoot(new(1, 2));
        root.SetScalar(0, 64, 42);
        root.GetPointer(1).SetText("managed");
        ListBuilder list = root.GetPointer(0).InitStructList(nodeCount, new(1, 1));
        for (int i = 0; i < list.Count; i++)
        {
            StructBuilder item = list.GetStruct(i);
            item.SetScalar(0, 32, (uint)i);
            item.GetPointer(0).SetText("child");
        }

        return builder.CopySegment();
    }

    private static byte[] Words(params ulong[] values)
    {
        byte[] bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), values[i]);
        }

        return bytes;
    }
}
