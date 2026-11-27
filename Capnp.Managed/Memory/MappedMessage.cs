using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace Capnp.Managed;

/// <summary>A disposable owner for a read-only view of a trusted immutable framed file.</summary>
public sealed class MappedMessage : IDisposable
{
    private readonly MemoryMappedViewAccessor view;
    private readonly SafeMemoryMappedViewHandle handle;
    private readonly long offset;
    private readonly int length;
    private readonly MessageLimits policy;
    private int disposed;

    private MappedMessage(MemoryMappedViewAccessor view, int length, MessageLimits policy)
    {
        this.view = view;
        handle = view.SafeMemoryMappedViewHandle;
        offset = view.PointerOffset;
        this.length = length;
        this.policy = policy;
    }

    public delegate TResult ByteInspection<TState, TResult>(ReadOnlySpan<byte> bytes, TState state);

    /// <remarks>The file must remain immutable. An external truncation can fault on Unix.</remarks>
    public static MappedMessage Open(string path, MessageLimits? limits = null)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Open(stream, limits);
    }

    /// <summary>Maps an already opened immutable file without changing the stream's ownership.</summary>
    public static MappedMessage Open(FileStream stream, MessageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MessageLimits policy = limits ?? MessageLimits.Default;
        limits?.Check();
        long length = stream.Length;
        if (length < 16 || length > policy.MaxFrameBytes)
        {
            throw Wire.Invalid("Invalid framed file length.");
        }

        using MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(stream, null, 0,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        return new(mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read), (int)length, policy);
    }

    /// <remarks>The borrowed view is scoped to a synchronous callback. A successful acquisition pins the view
    /// through the callback, including if the owner is disposed concurrently.</remarks>
    public TResult Inspect<TResult>(MessageInspection<TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return Inspect(inspect, static (message, callback) => callback(message));
    }

    public TResult Inspect<TState, TResult>(TState state, MessageInspection<TState, TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return InspectBytes((state, inspect, policy), static (bytes, capture) =>
            capture.inspect(FrameCodec.Read(bytes, capture.policy), capture.state));
    }

    public unsafe TResult InspectBytes<TState, TResult>(TState state, ByteInspection<TState, TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        byte* pointer = null;
        handle.AcquirePointer(ref pointer);
        try
        {
            return inspect(new ReadOnlySpan<byte>(pointer + offset, length), state);
        }
        finally
        {
            handle.ReleasePointer();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            view.Dispose();
        }
    }
}
