using System.Buffers.Binary;
using Capnp.Managed;

MessageBuilder builder = new();
builder.InitRoot(new(1, 0)).SetScalar(0, 64, 42);
byte[] canonical = CanonicalMessage.CreateSegment(new MessageReader(builder.CopySegment()));
byte[] single = FrameCodec.Write(new MessageReader(canonical));
ReadOnlyMemory<byte>[] far = [Words(0x0000000100000002), canonical];
ReadOnlyMemory<byte>[] doubleFar = [Words(0x0000000100000006), Words(0x0000000200000002, 0x0000000100000000), Words(42)];
byte[] multiple = FrameCodec.Write(new MessageReader(far));
byte[] doubleFrame = FrameCodec.Write(new MessageReader(doubleFar));
if (FrameCodec.Read(single).Root.AsStruct().ReadScalar(0, 64) != 42
    || FrameCodec.Read(multiple).Root.Kind != PointerKind.Far
    || FrameCodec.Read(doubleFrame).Root.Kind != PointerKind.DoubleFar
    || FrameCodec.Read(multiple).Root.AsStruct().ReadScalar(0, 64) != 42
    || !CanonicalMessage.CreateSegment(FrameCodec.Read(multiple)).AsSpan().SequenceEqual(canonical)
    || !CanonicalMessage.CreateSegment(FrameCodec.Read(doubleFrame)).AsSpan().SequenceEqual(canonical))
{
    throw new InvalidDataException("Managed Cap’n Proto NativeAOT read or canonicalization failed.");
}

DirectoryInfo directory = Directory.CreateTempSubdirectory("capnp-managed-aot-");
try
{
    string path = Path.Combine(directory.FullName, "message.bin");
    File.WriteAllBytes(path, doubleFrame);
    using MappedMessage mapped = MappedMessage.Open(path);
    if (mapped.Inspect(static message => message.Root.AsStruct().ReadScalar(0, 64)) != 42)
    {
        throw new InvalidDataException("Managed Cap’n Proto NativeAOT file-backed read failed.");
    }

    Console.WriteLine($"Capnp.Managed NativeAOT passed. canonical-bytes={canonical.Length} "
                      + $"framed-bytes={doubleFrame.Length} sha256={CanonicalMessage.HashSegment(canonical)}");
}
finally
{
    directory.Delete(recursive: true);
}

static byte[] Words(params ulong[] words)
{
    byte[] bytes = new byte[words.Length * 8];
    for (int i = 0; i < words.Length; i++)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), words[i]);
    }

    return bytes;
}
