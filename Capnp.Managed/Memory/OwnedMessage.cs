namespace Capnp.Managed;

public delegate TResult MessageInspection<TResult>(MessageReader message);
public delegate TResult MessageInspection<TState, TResult>(MessageReader message, TState state);

/// <summary>Privately owned immutable bytes. Concurrent inspections are safe.</summary>
public sealed class OwnedMessage
{
    private readonly byte[] bytes;
    private readonly MessageLimits limits;
    private readonly bool framed;

    private OwnedMessage(byte[] bytes, MessageLimits limits, bool framed)
    {
        this.bytes = bytes;
        this.limits = limits;
        this.framed = framed;
    }

    public static OwnedMessage CopyFrame(ReadOnlySpan<byte> frame, MessageLimits? limits = null)
    {
        MessageReader reader = FrameCodec.Read(frame, limits);
        return new(frame.ToArray(), reader.Limits, framed: true);
    }

    public static OwnedMessage CopySegment(ReadOnlySpan<byte> segment, MessageLimits? limits = null)
    {
        MessageReader reader = new(segment, limits);
        return new(segment.ToArray(), reader.Limits, framed: false);
    }

    /// <summary>Reads a bounded, complete frame from a potentially mutable file into private memory.</summary>
    public static OwnedMessage ReadBuffered(string path, MessageLimits? limits = null)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        OwnedMessage message = FrameCodec.Read(stream, limits);

        return stream.ReadByte() != -1 ? throw Wire.Invalid("The framed file has trailing bytes.") : message;
    }

    public TResult Inspect<TResult>(MessageInspection<TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return inspect(framed ? FrameCodec.Read(bytes, limits) : new(bytes, limits));
    }

    internal static OwnedMessage FromFrame(byte[] frame, MessageLimits limits) => new(frame, limits, framed: true);

    internal static OwnedMessage FromBuilder(byte[] segment, MessageLimits limits) => new(segment, limits, framed: false);
}
