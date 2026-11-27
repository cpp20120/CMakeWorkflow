using Capnp.Managed;

namespace CMakeWorkflow.Binary.Capnp;

/// <summary>Compatibility helpers for P0 unframed workflow segments.</summary>
public static class CapnpWire
{
    internal const int MaxCollection = 16_384;
    internal static MessageLimits Limits { get; } = MessageLimits.Default with
    {
        MaxMessageBytes = 16 * 1024 * 1024,
        MaxSegmentBytes = 16 * 1024 * 1024,
        MaxBuilderBytes = 16 * 1024 * 1024,
        MaxCanonicalBytes = 16 * 1024 * 1024,
        MaxTraversalWords = 4 * 1024 * 1024,
        MaxNesting = 32
    };

    internal static MessageLimits ViewLimits { get; } = Limits with
    {
        MaxListElements = MaxCollection,
        MaxTextBytes = MaxCollection
    };

    public static ReadOnlySpan<byte> Unframe(ReadOnlySpan<byte> frame)
    {
        MessageReader reader = FrameCodec.Read(frame, Limits);
        return reader.SegmentCount == 1
            ? reader.GetSegment(new(0))
            : throw new InvalidDataException("Expected one unpacked Cap’n Proto segment.");
    }

    public static byte[] Frame(ReadOnlySpan<byte> segment) => FrameCodec.Write(new MessageReader(segment, Limits));

    public static string HashCanonical(ReadOnlySpan<byte> canonicalSegment) => CanonicalMessage.HashSegment(canonicalSegment);

    public static byte[] Canonicalize(ReadOnlySpan<byte> segment) => CanonicalMessage.CreateSegment(new(segment, Limits));

    public static void Validate(ReadOnlySpan<byte> segment) => CanonicalMessage.Validate(new(segment, Limits));
}
