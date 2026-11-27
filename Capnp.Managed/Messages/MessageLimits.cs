namespace Capnp.Managed;

/// <summary>Wire and resource limits. The nesting ceiling protects the managed stack.</summary>
public sealed record MessageLimits
{
    public static MessageLimits Default { get; } = new();

    public int MaxMessageBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxSegmentBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxSegments { get; init; } = 512;
    public int MaxTraversalWords { get; init; } = 8 * 1024 * 1024;
    public int MaxNesting { get; init; } = 64;
    public int MaxListElements { get; init; } = 4 * 1024 * 1024;
    public int MaxTextBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxDataBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxCanonicalBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxBuilderBytes { get; init; } = 64 * 1024 * 1024;

    internal void Check()
    {
        if (MaxMessageBytes < 8 || MaxSegmentBytes < 8 || MaxSegments is < 1 or > 65_536
            || MaxTraversalWords < 1 || MaxNesting is < 1 or > 128 || MaxListElements < 0
            || MaxTextBytes < 0 || MaxDataBytes < 0 || MaxCanonicalBytes < 8 || MaxBuilderBytes < 8)
        {
            throw new ArgumentException("Invalid Cap’n Proto resource limits.");
        }
    }

    internal int MaxFrameBytes => (int)Math.Min(int.MaxValue, (long)MaxMessageBytes + FrameCodec.HeaderBytes(MaxSegments));
}
