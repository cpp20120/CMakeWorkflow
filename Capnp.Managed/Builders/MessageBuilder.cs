using System.Buffers.Binary;

namespace Capnp.Managed;

/// <summary>Mutable single-segment construction. Handles store offsets and survive arena growth.</summary>
public sealed class MessageBuilder
{
    private readonly WordArena arena;

    public MessageBuilder(MessageLimits? limits = null) : this(limits, 256) { }

    /// <summary>Reserves initial arena capacity up to the configured output limits.</summary>
    public MessageBuilder(MessageLimits? limits, int initialCapacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacityBytes);
        Limits = limits ?? MessageLimits.Default;
        limits?.Check();
        arena = new(Math.Min(Limits.MaxBuilderBytes, Math.Min(Limits.MaxMessageBytes, Limits.MaxSegmentBytes)),
            initialCapacityBytes);
    }

    public MessageLimits Limits { get; }
    public PointerBuilder Root => new(arena, Limits, 0);
    public StructBuilder InitRoot(StructLayout layout) => Root.InitStruct(layout);

    /// <summary>Copies the current segment into a privately owned immutable message.</summary>
    public OwnedMessage Snapshot() => OwnedMessage.FromBuilder(arena.Finish(), Limits);

    /// <summary>Copies the current segment for ownership by the caller.</summary>
    public byte[] CopySegment() => arena.Finish();
}

public readonly ref struct PointerBuilder
{
    private readonly WordArena arena;
    private readonly MessageLimits limits;
    private readonly int word;

    internal PointerBuilder(WordArena arena, MessageLimits limits, int word)
    {
        this.arena = arena;
        this.limits = limits;
        this.word = word;
    }

    public void Clear() => arena.Write(word, 0);

    /// <summary>Copies a reachable immutable value into this arena under both messages’ resource limits.</summary>
    public void SetCopy(PointerReader source)
    {
        if (source.IsNull)
        {
            Clear();
        }
        else
        {
            int temporary = arena.Allocate(1);
            CanonicalMessage.CopyTo(source, arena, temporary);
            arena.PublishPointer(temporary, word);
        }
    }


    public StructBuilder InitStruct(StructLayout layout) => new(arena, limits, arena.Struct(word, layout));

    public ListBuilder InitStructList(int count, StructLayout layout)
    {
        CheckCount(count, 0x3fffffff);
        ListLocation list = arena.Composite(word, count, layout);
        return new(arena, limits, list.Start.Offset.Value, count, list.Kind, layout);
    }

    public ListBuilder InitList(ListElementSize size, int count)
    {
        if (size == ListElementSize.Composite)
        {
            throw new ArgumentException("Use InitStructList with an explicit layout.", nameof(size));
        }

        CheckCount(count, 0x1fffffff);
        int start = arena.List(word, size, count, Wire.Words(size, count));
        return new(arena, limits, start, count, size, default);
    }

    public void SetText(string? value)
    {
        if (value is null)
        {
            Clear();
            return;
        }

        int length = Wire.Utf8.GetByteCount(value);
        if (length >= limits.MaxTextBytes)
        {
            throw new ArgumentException("Text exceeds its byte limit.", nameof(value));
        }

        int start = AllocateBlob(length + 1);
        _ = Wire.Utf8.GetBytes(value, arena.Slice(start, length));
    }

    public void SetData(scoped ReadOnlySpan<byte> value)
    {
        if (value.Length > limits.MaxDataBytes)
        {
            throw new ArgumentException("Data exceeds its byte limit.", nameof(value));
        }

        value.CopyTo(arena.Slice(AllocateBlob(value.Length), value.Length));
    }

    private int AllocateBlob(int length)
    {
        CheckCount(length, 0x1fffffff);
        return arena.List(word, ListElementSize.Byte, length, Wire.Words(ListElementSize.Byte, length));
    }

    private void CheckCount(int count, int maxWireCount)
    {
        if (count < 0 || count > Math.Min(maxWireCount, limits.MaxListElements))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
    }
}

public readonly ref struct StructBuilder
{
    private readonly WordArena arena;
    private readonly MessageLimits limits;
    private readonly StructLocation value;

    internal StructBuilder(WordArena arena, MessageLimits limits, StructLocation value)
    {
        this.arena = arena;
        this.limits = limits;
        this.value = value;
    }

    public void SetScalar(int bitOffset, int width, ulong fieldValue, ulong defaultValue = 0)
    {
        Wire.CheckScalar(bitOffset, width);
        if ((long)bitOffset + width > value.Data * 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitOffset));
        }

        ListBuilder.WriteScalar(arena.Slice(value.Start.Offset.Value, value.Data * 8), bitOffset, width,
            fieldValue ^ defaultValue);
    }

    public PointerBuilder GetPointer(int fieldIndex)
    {
        Wire.CheckIndex(fieldIndex, value.Pointers);
        return new(arena, limits, value.Pointer(fieldIndex).Offset.Value);
    }
}

public readonly ref struct ListBuilder
{
    private readonly WordArena arena;
    private readonly MessageLimits limits;
    private readonly int start;
    private readonly ListElementSize size;
    private readonly StructLayout layout;

    internal ListBuilder(WordArena arena, MessageLimits limits, int start, int count, ListElementSize size,
        StructLayout layout)
    {
        this.arena = arena;
        this.limits = limits;
        this.start = start;
        this.size = size;
        this.layout = layout;
        Count = count;
    }

    public int Count { get; }

    public StructBuilder GetStruct(int index)
    {
        Wire.CheckIndex(index, Count);
        return size == ListElementSize.Composite
            ? new(arena, limits, new(new(default, new(start + (index * (layout.DataWords + layout.PointerWords)))), layout))
            : throw new InvalidOperationException("This builder does not hold a struct list.");
    }

    public PointerBuilder GetPointer(int index)
    {
        Wire.CheckIndex(index, Count);
        return size == ListElementSize.PointerWord
            ? new(arena, limits, start + index)
            : throw new InvalidOperationException("This builder does not hold a pointer list.");
    }

    public void SetScalar(int index, ulong value)
    {
        Wire.CheckIndex(index, Count);
        if (size is ListElementSize.PointerWord or ListElementSize.Composite or ListElementSize.Void)
        {
            throw new InvalidOperationException("This builder does not hold a scalar list.");
        }

        int width = Wire.Bits(size);
        long bitOffset = (long)index * width;
        int word = (int)(bitOffset / 64);
        WriteScalar(arena.Slice(start + word, 8), (int)(bitOffset & 63), width, value);
    }

    internal static void WriteScalar(Span<byte> bytes, int bitOffset, int width, ulong value)
    {
        Span<byte> field = bytes[(bitOffset / 8)..];
        switch (width)
        {
            case 1:
                int mask = 1 << (bitOffset & 7);
                field[0] = (byte)((field[0] & ~mask) | ((value & 1) == 0 ? 0 : mask));
                break;
            case 8:
                field[0] = (byte)value;
                break;
            case 16:
                BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)value);
                break;
            case 32:
                BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)value);
                break;
            case 64:
                BinaryPrimitives.WriteUInt64LittleEndian(field, value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(width));
        }
    }
}
