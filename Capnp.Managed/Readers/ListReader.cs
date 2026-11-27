using System.Buffers.Binary;

namespace Capnp.Managed;

/// <summary>A borrowed indexed list. Compatible schema upgrades are interpreted on access.</summary>
public readonly ref struct ListReader
{
    private readonly MessageReader message;
    private readonly ListLocation list;

    internal ListReader(MessageReader message, ListLocation list)
    {
        this.message = message;
        this.list = list;
    }

    public int Count => list.Count;
    public ListElementSize ElementSize => list.Kind;

    public StructReader GetStruct(int index)
    {
        Wire.CheckIndex(index, Count);
        return list.Kind switch
        {
            ListElementSize.Composite => new(message, list.Item(index)),
            ListElementSize.PointerWord => new(message, default, list.Start.Add(index), 1),
            ListElementSize.Void => default,
            ListElementSize.Bit => throw Wire.Invalid("Bool lists cannot be read as structs."),
            ListElementSize.Byte or ListElementSize.TwoBytes or ListElementSize.FourBytes or ListElementSize.EightBytes =>
                new(message, PrimitiveElement(index), default, 0),
            _ => throw Wire.Invalid("Unsupported list element size.")
        };
    }

    public PointerReader GetPointer(int index)
    {
        Wire.CheckIndex(index, Count);
        return list.Kind switch
        {
            ListElementSize.PointerWord => new(message, list.Start.Add(index)),
            ListElementSize.Composite => list.Layout.PointerWords > 0
                ? new(message, list.Item(index).Pointer(0))
                : throw Wire.Invalid("The struct list has no pointer prefix."),
            ListElementSize.Void or ListElementSize.Bit or ListElementSize.Byte or ListElementSize.TwoBytes
                or ListElementSize.FourBytes or ListElementSize.EightBytes =>
                throw Wire.Invalid("Expected a pointer list or upgraded struct list."),
            _ => throw Wire.Invalid("Unknown list element size.")
        };
    }

    public ulong ReadScalar(int index, int width)
    {
        Wire.CheckIndex(index, Count);
        // The wire reader validated the containing list range when resolving the pointer.
        if (width == 32 && list.Kind == ListElementSize.FourBytes)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(message.Bytes(list.Start, Count * 4)[(index * 4)..]);
        }

        if (width == 16 && list.Kind == ListElementSize.TwoBytes)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(message.Bytes(list.Start, Count * 2)[(index * 2)..]);
        }

        Wire.CheckScalar(0, width);
        return list.Kind switch
        {
            ListElementSize.Bit => width == 1
                ? Wire.ReadScalar(message.Bytes(list.Start, (Count + 7) / 8), index, 1)
                : throw Wire.Invalid("Expected a bit access."),
            ListElementSize.Composite => width != 1 && list.Layout.DataWords > 0
                ? GetStruct(index).ReadScalar(0, width)
                : throw Wire.Invalid("Incompatible struct list data prefix."),
            ListElementSize.Byte or ListElementSize.TwoBytes or ListElementSize.FourBytes or ListElementSize.EightBytes =>
                width != 1 && width <= Wire.Bits(list.Kind)
                    ? Wire.ReadScalar(PrimitiveElement(index), 0, width)
                    : throw Wire.Invalid("Incompatible primitive list width."),
            ListElementSize.Void or ListElementSize.PointerWord => throw Wire.Invalid("Expected a primitive list."),
            _ => throw Wire.Invalid("Unknown list element size.")
        };
    }

    internal void RequireCompatible(ListElementSize expected)
    {
        if (list.Kind == expected || list == default)
        {
            return;
        }

        bool compatible = list.Kind == ListElementSize.Composite
            ? expected switch
            {
                ListElementSize.Void => true,
                ListElementSize.PointerWord => list.Layout.PointerWords > 0,
                ListElementSize.Byte or ListElementSize.TwoBytes or ListElementSize.FourBytes or ListElementSize.EightBytes =>
                    list.Layout.DataWords > 0,
                ListElementSize.Bit or ListElementSize.Composite => false,
                _ => false
            }
            : list.Kind != ListElementSize.Bit && expected != ListElementSize.Bit
                && (expected is ListElementSize.Void or ListElementSize.Composite
                    || (list.Kind != ListElementSize.PointerWord && expected != ListElementSize.PointerWord
                        && Wire.Bits(list.Kind) >= Wire.Bits(expected)));
        if (!compatible)
        {
            throw Wire.Invalid("Unexpected list element size.");
        }
    }

    private ReadOnlySpan<byte> PrimitiveElement(int index)
    {
        int width = Wire.Bits(list.Kind) / 8;
        // The encoded list was bounds checked before constructing this reader.
        int bytes = (int)((long)width * Count);
        return message.Bytes(list.Start, bytes).Slice(index * width, width);
    }
}
