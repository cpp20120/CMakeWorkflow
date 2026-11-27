using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Capnp.Generated.Probe;
using Capnp.Managed.CodeGen.Fixtures;

namespace Capnp.Managed.CodeGen.Benchmarks;

[MemoryDiagnoser]
[SuppressMessage("Maintainability", "CA1515", Justification =
    "BenchmarkDotNet emits a separate benchmark host that derives from this class.")]
public class AccessorBenchmarks
{
    private byte[] message = null!;
    private byte[] listMessage = null!;

    [GlobalSetup]
    public void Setup()
    {
        message = Fixture.Build(transitions: false);
        MessageBuilder builder = new();
        Probe.SamplesListBuilder values = Probe.InitRoot(builder).InitSamples(512);
        for (int i = 0; i < values.Count; i++)
        {
            values.Set(i, (ushort)i);
        }

        listMessage = builder.CopySegment();
        if (ManualScalar() != GeneratedScalar() || ManualIndexed() != GeneratedIndexed()
            || ManualGroupText() != GeneratedGroupText() || ManualEnumeration() != GeneratedEnumeration()
            || ManualEnumeration() != GeneratedIndexedEnumeration() || ManualDiscriminant() != GeneratedDiscriminant())
        {
            throw new InvalidDataException("Benchmark fixture mismatch.");
        }
    }

    [Benchmark]
    public uint ManualScalar() => (uint)new MessageReader(message).Root.AsStruct().ReadScalar(160, 32, 70_000);

    [Benchmark]
    public uint GeneratedScalar() => Probe.ReadRoot(new MessageReader(message)).U32;

    [Benchmark]
    public uint ManualIndexed() =>
        (uint)new MessageReader(message).Root.AsStruct().GetPointer(6)
            .AsList(ListElementSize.Composite).GetStruct(1).GetPointer(0).AsText().Length;

    [Benchmark]
    public uint GeneratedIndexed() => (uint)Probe.ReadRoot(new MessageReader(message)).Children[1].NoteUtf8.Length;

    [Benchmark]
    public ushort ManualDiscriminant() => (ushort)new MessageReader(message).Root.AsStruct().ReadScalar(304, 16);

    [Benchmark]
    public ushort GeneratedDiscriminant() => (ushort)Probe.ReadRoot(new MessageReader(message)).Payload.Which;

    [Benchmark]
    public int ManualGroupText()
    {
        StructReader root = new MessageReader(message).Root.AsStruct();
        return root.ReadScalar(304, 16) == 3
            ? root.GetPointer(10).AsText().Length
            : throw new InvalidOperationException("Inactive union member.");
    }

    [Benchmark]
    public int GeneratedGroupText() => Probe.ReadRoot(new MessageReader(message)).Payload.Details.Inner.TextUtf8.Length;

    [Benchmark]
    public int ManualEnumeration()
    {
        ListReader list = new MessageReader(listMessage).Root.AsStruct().GetPointer(3).AsList(ListElementSize.TwoBytes);
        int sum = 0;
        for (int i = 0; i < list.Count; i++)
        {
            sum += (ushort)list.ReadScalar(i, 16);
        }

        return sum;
    }

    [Benchmark]
    public int GeneratedIndexedEnumeration()
    {
        Probe.SamplesListReader list = Probe.ReadRoot(new MessageReader(listMessage)).Samples;
        int sum = 0;
        for (int i = 0; i < list.Count; i++)
        {
            sum += list[i];
        }

        return sum;
    }

    [Benchmark]
    public int GeneratedEnumeration()
    {
        int sum = 0;
        foreach (ushort value in Probe.ReadRoot(new MessageReader(listMessage)).Samples)
        {
            sum += value;
        }

        return sum;
    }
}
