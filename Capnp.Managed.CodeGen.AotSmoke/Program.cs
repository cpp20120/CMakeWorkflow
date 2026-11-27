using Capnp.Managed;
using Capnp.Managed.CodeGen.Fixtures;

byte[] canonical = CanonicalMessage.CreateSegment(new MessageReader(Fixture.Build(transitions: true)));
byte[] farRoot = [2, 0, 0, 0, 1, 0, 0, 0];
byte[] framed = FrameCodec.Write(new MessageReader([farRoot, canonical]));
MessageReader multi = FrameCodec.Read(framed);
if (multi.SegmentCount != 2 || multi.Root.Kind != PointerKind.Far || !Fixture.Matches(multi)
    || !Fixture.Defaults(new MessageReader(new byte[8]))
    || !CanonicalMessage.CreateSegment(multi).AsSpan().SequenceEqual(canonical))
{
    throw new InvalidDataException("Generated NativeAOT fixture failed.");
}

DirectoryInfo directory = Directory.CreateTempSubdirectory("capnp-codegen-aot-");
try
{
    string path = Path.Combine(directory.FullName, "fixture.bin");
    File.WriteAllBytes(path, framed);
    using MappedMessage mapped = MappedMessage.Open(path);
    if (!mapped.Inspect(Fixture.Matches))
    {
        throw new InvalidDataException("Generated mapped NativeAOT read failed.");
    }

    Console.WriteLine($"Generated NativeAOT bindings passed. canonical={canonical.Length} sha256={CanonicalMessage.HashSegment(canonical)}");
}
finally
{
    directory.Delete(recursive: true);
}
