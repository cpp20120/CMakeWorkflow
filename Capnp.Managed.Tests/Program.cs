using System.Buffers.Binary;
using System.Security.Cryptography;
using Capnp.Managed;
using Capnp.Managed.Tests;

byte[] staged = Probe.Build(reverse: false).CopySegment();
byte[] reverse = Probe.Build(reverse: true).CopySegment();
Check(!staged.AsSpan().SequenceEqual(reverse), "Allocation-order fixture differed only in name.");
byte[] canonical = CanonicalMessage.CreateSegment(new MessageReader(staged));
Check(canonical.AsSpan().SequenceEqual(CanonicalMessage.CreateSegment(new MessageReader(reverse))),
    "Builder allocation order affects canonical bytes.");
Check(canonical.AsSpan().SequenceEqual(CanonicalMessage.CreateSegment(new MessageReader(canonical))),
    "Canonicalization is not idempotent.");
Check(CanonicalMessage.HashSegment(canonical) == "18957059d13e675d611102a9307e3af301b65cb509c56ccaf4874d164500ea07",
    "Official canonical digest changed.");
CheckProbe(new MessageReader(canonical));

for (int seed = 0; seed < 64; seed++)
{
    byte[] ordering = Sample(seed + 875);
    int[] slots = [0, 1, 2, 3, 4, 5, 6, 7];
    MessageBuilder sample = new();
    StructBuilder sampleRoot = sample.InitRoot(new(0, (ushort)slots.Length));
    for (int i = slots.Length - 1; i >= 0; i--)
    {
        int selected = ordering[i] % (i + 1);
        (slots[selected], slots[i]) = (slots[i], slots[selected]);
        sampleRoot.GetPointer(slots[i]).InitStruct(new(1, 0)).SetScalar(0, 64, (uint)slots[i] + 1u);
    }

    MessageBuilder ordered = new();
    StructBuilder orderedRoot = ordered.InitRoot(new(0, (ushort)slots.Length));
    for (int i = 0; i < slots.Length; i++)
    {
        orderedRoot.GetPointer(i).InitStruct(new(1, 0)).SetScalar(0, 64, (uint)i + 1u);
    }

    Check(CanonicalMessage.CreateSegment(new MessageReader(sample.CopySegment())).AsSpan()
            .SequenceEqual(CanonicalMessage.CreateSegment(new MessageReader(ordered.CopySegment()))),
        "Seeded builder order changed canonical bytes.");
}


byte[] emptyViaFar = FrameCodec.Write(new MessageReader([Words(Far(1, 0)), Words(0)]));
Check(CanonicalMessage.CreateSegment(FrameCodec.Read(emptyViaFar)).AsSpan().SequenceEqual(Words(0xfffffffc)),
    "The zero single-far pad denotes an empty struct.");

MessageBuilder builder = new();
StructBuilder root = builder.InitRoot(new(1, 3));
Check(new Probe(new MessageReader(builder.CopySegment()).Root.AsStruct()).Number == 17, "Absent field default.");
Check(new Probe(new MessageReader(builder.CopySegment()).Root.AsStruct()).Label.SequenceEqual("schema-default"u8),
    "Missing pointer uses the schema's separate default object.");
root.GetPointer(0).SetText("");
Check(new Probe(new MessageReader(builder.CopySegment()).Root.AsStruct()).Label.IsEmpty, "Present empty text.");
root.GetPointer(1).InitStruct(default);
Check(new MessageReader(builder.CopySegment()).Root.AsStruct().GetPointer(1).Kind == PointerKind.Struct,
    "Empty structs have a non-null pointer.");
root.GetPointer(0).Clear();
Check(new MessageReader(builder.CopySegment()).Root.AsStruct().GetPointer(0).IsNull, "Clear writes null.");
root.SetScalar(0, 32, 17, 17);
Check(new MessageReader(builder.CopySegment()).Root.AsStruct().ReadDataWord(new(0)) == 0,
    "Storing schema default must write zero.");
root.SetScalar(0, 32, 28, 17);
Check(new MessageReader(builder.CopySegment()).Root.AsStruct().ReadScalar(0, 32, 17) == 28, "XOR semantics.");

ListBuilder oldValues = root.GetPointer(0).InitList(ListElementSize.TwoBytes, 3);
oldValues.SetScalar(2, 999);
ListBuilder pointers = root.GetPointer(1).InitList(ListElementSize.PointerWord, 2);
pointers.GetPointer(0).SetText("before");
OwnedMessage snapshot = builder.Snapshot();
root.GetPointer(2).SetData(new byte[32_000]);
pointers.GetPointer(1).SetText("after");
oldValues.SetScalar(0, 765);
Check(snapshot.Inspect(static message =>
    message.Root.AsStruct().GetPointer(1).AsList().GetPointer(1).IsNull), "Snapshot mutated with builder.");
StructReader grown = new MessageReader(builder.CopySegment()).Root.AsStruct();
StructReader upgraded = grown.GetPointer(0).AsList(ListElementSize.Composite).GetStruct(0);
Check(upgraded.ReadScalar(0, 16) == 765 && upgraded.ReadScalar(16, 16, 99) == 99,
    "Primitive struct projection and growth-stable handle.");
ListReader promoted = grown.GetPointer(1).AsList(ListElementSize.Composite);
Check(promoted.GetStruct(0).GetPointer(0).AsText().SequenceEqual("before"u8)
    && promoted.GetStruct(1).GetPointer(0).AsText().SequenceEqual("after"u8),
    "Pointer-list upgrade and growth-stable handle.");
ListReader composite = new MessageReader(canonical).Root.AsStruct().GetPointer(4).AsList(ListElementSize.TwoBytes);
Check(composite.ReadScalar(1, 16) == 513 && composite.GetPointer(0).AsText().SequenceEqual("entry"u8),
    "Composite projection through an older scalar or pointer schema.");
Reject(() => new MessageReader(canonical).Root.AsStruct().GetPointer(2).AsList(ListElementSize.Composite), "Bool upgrade.");

byte[] basic = Words(Struct(0, 1, 0), 7);
byte[] multiFrame = [];
for (int i = 0; i < 128; i++)
{
    byte[] sample = Sample(i);
    int count = 3 + (sample[0] % 5);
    int targetId = 1 + (sample[1] % (count - 1));
    int padId = 1 + (targetId % (count - 1));
    int word = sample[2] % 7;
    int pad = sample[3] % 5;
    ReadOnlyMemory<byte>[] segments = new ReadOnlyMemory<byte>[count];
    ulong[] target = new ulong[word + 1];
    target[word] = 7;
    if (i % 2 == 0)
    {
        // The landing pad follows the object, so its near pointer is negative.
        segments[0] = Words(Far(targetId, word + 1));
        segments[targetId] = Words([.. target, Struct(-2, 1, 0)]);
    }
    else
    {
        ulong[] landing = new ulong[pad + 2];
        landing[pad] = Far(targetId, word);
        landing[pad + 1] = Struct(0, 1, 0);
        segments[0] = Words(Far(padId, pad, doubleFar: true));
        segments[targetId] = Words(target);
        segments[padId] = Words(landing);
    }

    MessageReader segmented = new(segments);
    Check(segmented.Root.AsStruct().ReadScalar(0, 64) == 7, "Segmented field value.");
    multiFrame = FrameCodec.Write(segmented);
    if ((count & 1) == 0)
    {
        Check(BinaryPrimitives.ReadUInt32LittleEndian(multiFrame.AsSpan((count + 1) * 4)) == 0, "Table padding.");
    }

    MessageReader framed = FrameCodec.Read(multiFrame);
    Check(framed.SegmentCount == count && framed.GetSegment(new((uint)targetId)).Length > 0, "Segment lookup.");
    Check(CanonicalMessage.CreateSegment(framed).AsSpan().SequenceEqual(basic), "Segment placement affected bytes.");
}

byte[] bitmap = Words(Struct(0, 0, 1), 0x0000001900000001, ulong.MaxValue); // Three booleans.
byte[] bitmapCanonical = CanonicalMessage.CreateSegment(new MessageReader(bitmap));
Check(BinaryPrimitives.ReadUInt64LittleEndian(bitmapCanonical.AsSpan(16)) == 7, "Zero unused bit and word padding.");
Check(new MessageReader(bitmapCanonical).Root.AsStruct().GetPointer(0).AsList().ReadScalar(2, 1) == 1, "Packed bit access.");
byte[] emptyViaDouble = FrameCodec.Write(new MessageReader([Words(Far(1, 0, true)), Words(Far(2, 0), 0), default]));
Check(CanonicalMessage.CreateSegment(FrameCodec.Read(emptyViaDouble)).AsSpan().SequenceEqual(Words(0xfffffffc)),
    "The zero double-far tag denotes an empty struct.");

byte[] frame = FrameCodec.Write(new MessageReader(canonical));
using (MemoryStream stream = new([.. frame, .. multiFrame]))
{
    Check(FrameCodec.Read(stream).Inspect(static message => message.Root.AsStruct().ReadScalar(0, 32, 17)) == 31,
        "Sequential stream reads first frame.");
    Check(FrameCodec.Read(stream).Inspect(static message => message.Root.AsStruct().ReadScalar(0, 64)) == 7,
        "Sequential stream reads next frame.");
    Reject(() => FrameCodec.Read(stream), "EOF on streaming frame.");
}

OwnedMessage owned = OwnedMessage.CopyFrame(frame);
byte[] mutable = (byte[])frame.Clone();
OwnedMessage isolated = OwnedMessage.CopyFrame(mutable);
Array.Fill(mutable, (byte)0);
Check(isolated.Inspect(static message => message.Root.AsStruct().ReadScalar(0, 32, 17)) == 31, "Copy ownership.");
DirectoryInfo directory = Directory.CreateTempSubdirectory("capnp-managed-tests-");
try
{
    string path = Path.Combine(directory.FullName, "mapped.bin");
    File.WriteAllBytes(path, multiFrame);
    using MappedMessage mapped = MappedMessage.Open(path);
    Check(mapped.Inspect(message =>
    {
        mapped.Dispose();
        return message.Root.AsStruct().ReadScalar(0, 64);
    }) == 7, "A mapped inspection pins its view through disposal.");
    try
    {
        _ = mapped.Inspect(static _ => 0);
        throw new InvalidOperationException("Disposed owner accepted an inspection.");
    }
    catch (ObjectDisposedException)
    {
    }

    Check(OwnedMessage.ReadBuffered(path).Inspect(static message => message.Root.AsStruct().ReadScalar(0, 64)) == 7,
        "Buffered ownership.");
    File.WriteAllBytes(path, multiFrame[..^1]);
    Reject(() => OwnedMessage.ReadBuffered(path), "Buffered reads reject truncation.");

    if (args is ["--oracle", string executable])
    {
        Oracle.Run(executable, directory.FullName, canonical, CheckProbe);
    }
    else if (args.Length != 0)
    {
        throw new ArgumentException("Use --oracle <capnp-managed-oracle> to require independent interoperability.");
    }
}
finally
{
    directory.Delete(recursive: true);
}

for (int length = 0; length < frame.Length; length++)
{
    int cut = length;
    Reject(() => FrameCodec.Read(frame.AsSpan(0, cut)), "Frame truncation at every byte boundary.");
}
for (int length = 0; length < multiFrame.Length; length++)
{
    int cut = length;
    Reject(() => FrameCodec.Read(multiFrame.AsSpan(0, cut)), "Segmented frame truncation at every byte boundary.");
}

Reject(() => FrameCodec.Read(Words(ulong.MaxValue, 0)), "Overflowing segment count.");
Reject(() => FrameCodec.Read(Words(0xffffffff00000000, 0)), "Overflowing segment length.");
Reject(() => FrameCodec.Read(multiFrame, MessageLimits.Default with { MaxSegments = 1 }), "Segment count policy.");
Reject(() => FrameCodec.Read(multiFrame, MessageLimits.Default with { MaxMessageBytes = 8 }), "Cumulative size policy.");
Reject(() => FrameCodec.Read(frame, MessageLimits.Default with { MaxSegmentBytes = 8 }), "Segment size policy.");
Reject(() => CanonicalMessage.CreateSegment(new MessageReader(frame.AsSpan(8),
    MessageLimits.Default with { MaxCanonicalBytes = 16 })), "Canonical output policy.");
Reject(() => CanonicalMessage.Validate(new MessageReader(canonical,
    MessageLimits.Default with { MaxListElements = 1 })), "List element policy.");
Reject(() => FrameCodec.Read(frame, MessageLimits.Default with { MaxTextBytes = 4 })
    .Root.AsStruct().GetPointer(0).AsText(), "Text byte policy.");
Reject(() => FrameCodec.Read(frame, MessageLimits.Default with { MaxDataBytes = 4 })
    .Root.AsStruct().GetPointer(5).AsData(), "Data byte policy.");
Reject(() => new MessageBuilder(MessageLimits.Default with { MaxBuilderBytes = 8 }).InitRoot(new(1, 0)), "Builder byte policy.");

Reject(() => CanonicalMessage.Validate(new MessageReader(Words(3))), "Capability pointer.");
Reject(() => CanonicalMessage.Validate(new MessageReader(Words(7))), "Unknown pointer type.");
Reject(() => CanonicalMessage.Validate(new MessageReader(Words(Far(1, 0)))), "Missing far segment.");
Reject(() => CheckFar(Far(1, 1), Words(Struct(0, 1, 0))), "Landing pad outside its segment.");
Reject(() => CheckFar(Far(1, 0, true), Words(Far(0, 0))), "Truncated double-far.");
Reject(() => CheckFar(Far(1, 0, true), Words(Far(0, 0, true), Struct(0, 1, 0))), "Double-far cannot chain.");
Reject(() => CheckFar(Far(1, 0, true), Words(Far(0, 0), Struct(1, 1, 0))), "Nonzero double-far tag offset.");
Reject(() => CheckFar(Far(1, 0), Words(Far(0, 0))), "Far landing pad cannot chain.");
Reject(() => CanonicalMessage.Validate(new MessageReader(Words(Struct(0, 0, 1), Struct(-1, 0, 1)))), "Cycle.");
Reject(() => CanonicalMessage.Validate(new MessageReader(Words(0x0000000f00000001, 0xfffffffffffffffc, 0))),
    "Malformed inline composite tag.");
Reject(() => CanonicalMessage.Validate(new MessageReader(Words(0x0000000700000001, 0x0000000000010000),
    MessageLimits.Default with { MaxTraversalWords = 16 })), "Zero-width elements charge traversal.");
byte[] alias = Words(Struct(0, 0, 2), Struct(1, 1, 0), Struct(0, 1, 0), 17);
CanonicalMessage.Validate(new MessageReader(alias));
Reject(() => CanonicalMessage.Validate(new MessageReader(alias,
    MessageLimits.Default with { MaxTraversalWords = 6 })), "Aliased target must be charged on every visit.");
byte[] overlap = Words(Struct(0, 0, 2), Struct(1, 2, 0), Struct(1, 1, 0), 9, 17);
Reject(() => CanonicalMessage.Validate(new MessageReader(overlap,
    MessageLimits.Default with { MaxTraversalWords = 7 })), "Overlapping object ranges must be charged on every visit.");
Reject(() => CanonicalMessage.Validate(new MessageReader(canonical,
    MessageLimits.Default with { MaxNesting = 1 })), "Pointer nesting limit.");
Check(new MessageReader(Words(Struct(0, 1, 1), 92, 3)).Root.AsStruct().ReadScalar(0, 64) == 92,
    "Local field access does not walk unrelated invalid pointers.");

for (int i = 0; i < 5000; i++)
{
    byte[] seed = Sample(i + 5000);
    byte[] input = i % 2 == 0 ? (byte[])multiFrame.Clone() : new byte[seed[0]];
    if (i % 2 == 0)
    {
        for (int j = 0; j < 8; j++)
        {
            int position = BinaryPrimitives.ReadUInt16LittleEndian(seed.AsSpan(j * 3)) % input.Length;
            input[position] ^= seed[(j * 3) + 2];
        }
    }
    else
    {
        for (int j = 0; j < input.Length; j += seed.Length)
        {
            byte[] block = Sample((i * 256) + j);
            block.AsSpan(0, Math.Min(block.Length, input.Length - j)).CopyTo(input.AsSpan(j));
        }
    }

    try
    {
        MessageReader message = FrameCodec.Read(input);
        byte[] result = CanonicalMessage.CreateSegment(message);
        Check(result.AsSpan().SequenceEqual(CanonicalMessage.CreateSegment(new MessageReader(result))),
            "Fuzz canonical idempotence.");
    }
    catch (InvalidDataException)
    {
        // Refused malformed wire input.
    }
}

Check(owned.Inspect(static message => message.Root.AsStruct().GetPointer(3).AsList().Count) == 3, "Owned data survives.");
Console.WriteLine("Managed wire, ownership, all limits and 5,000 seeded frame cases passed.");

static void CheckProbe(MessageReader reader)
{
    Probe probe = new(reader.Root.AsStruct());
    Check(probe.Number == 31 && probe.Label.SequenceEqual("managed"u8) && probe.LeafNumber == 0x1122334455667788
        && probe.Flags.Count == 11 && probe.Flags.ReadScalar(10, 1) == 1 && probe.Flags.ReadScalar(1, 1) == 0
        && probe.Samples.Count == 3 && probe.Samples.ReadScalar(2, 16) == 255 && probe.Entries.Count == 2
        && probe.Entries.GetStruct(1).ReadScalar(0, 16) == 513 && probe.Blob.Length == 64 && probe.Blob[63] == 63,
        "Probe schema fields differ.");
}

static void CheckFar(ulong pointer, byte[] pad) =>
    CanonicalMessage.Validate(new MessageReader([Words(pointer), pad]));

static ulong Far(int segment, int word, bool doubleFar = false) =>
    ((ulong)(uint)segment << 32) | ((ulong)(uint)word << 3) | (doubleFar ? 6UL : 2UL);

static ulong Struct(int offset, ushort data, ushort pointers) =>
    unchecked((uint)(offset << 2)) | ((ulong)data << 32) | ((ulong)pointers << 48);

static byte[] Words(params ulong[] words)
{
    byte[] bytes = new byte[words.Length * 8];
    for (int i = 0; i < words.Length; i++)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), words[i]);
    }

    return bytes;
}

static byte[] Sample(int seed)
{
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(buffer, seed);
    return SHA256.HashData(buffer);
}

static void Reject(Action action, string reason)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return;
    }

    throw new InvalidOperationException(reason);
}

static void Check(bool check, string reason)
{
    if (!check)
    {
        throw new InvalidOperationException(reason);
    }
}
