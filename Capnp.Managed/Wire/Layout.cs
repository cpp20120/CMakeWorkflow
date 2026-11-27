using System.Buffers.Binary;
using System.Text;

namespace Capnp.Managed;

public readonly record struct SegmentId(uint Value);
public readonly record struct WordOffset(int Value);
public readonly record struct WordAddress(SegmentId Segment, WordOffset Offset)
{
    internal static WordAddress Root => default;
    internal WordAddress Add(int words) => new(Segment, new(Offset.Value + words));
}

public readonly record struct StructLayout(ushort DataWords, ushort PointerWords);

public enum ListElementSize { Void, Bit, Byte, TwoBytes, FourBytes, EightBytes, PointerWord, Composite }
public enum PointerKind { Null, Struct, List, Far, DoubleFar, Capability, Unknown }

internal readonly record struct StructLocation(WordAddress Start, StructLayout Layout)
{
    internal int Data => Layout.DataWords;
    internal int Pointers => Layout.PointerWords;
    internal WordAddress Pointer(int index) => Start.Add(Data + index);
}

internal readonly record struct ListLocation(WordAddress Start, int Count, int Words, ListElementSize Kind, StructLayout Layout)
{
    internal StructLocation Item(int index) => new(Start.Add(index * (Layout.DataWords + Layout.PointerWords)), Layout);
}

internal readonly record struct ResolvedPointer(ulong Tag, WordAddress Target, int LandingWords);

internal static class Wire
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static PointerKind Kind(ulong word) => (word & 7) switch
    {
        _ when word == 0 => PointerKind.Null,
        0 or 4 => PointerKind.Struct,
        1 or 5 => PointerKind.List,
        2 => PointerKind.Far,
        6 => PointerKind.DoubleFar,
        _ when (uint)word == 3 => PointerKind.Capability,
        _ => PointerKind.Unknown
    };

    internal static int SignedOffset(ulong word) => unchecked((int)word) >> 2;
    internal static uint EncodeOffset(int offset) => unchecked((uint)(offset << 2));
    internal static StructLayout Layout(ulong word) => new((ushort)(word >> 32), (ushort)(word >> 48));
    internal static ulong EncodeLayout(StructLayout layout) =>
        ((ulong)layout.DataWords << 32) | ((ulong)layout.PointerWords << 48);

    internal static int Bits(ListElementSize size) => size switch
    {
        ListElementSize.Void => 0,
        ListElementSize.Bit => 1,
        ListElementSize.Byte => 8,
        ListElementSize.TwoBytes => 16,
        ListElementSize.FourBytes => 32,
        ListElementSize.EightBytes or ListElementSize.PointerWord => 64,
        ListElementSize.Composite => throw Invalid("Composite lists have a struct stride."),
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };

    internal static int Words(ListElementSize size, int count) => (int)((((long)Bits(size) * count) + 63) / 64);

    internal static ulong ReadScalar(ReadOnlySpan<byte> data, int bitOffset, int width) => width switch
    {
        1 => (ulong)((data[bitOffset / 8] >> (bitOffset & 7)) & 1),
        8 => data[bitOffset / 8],
        16 => BinaryPrimitives.ReadUInt16LittleEndian(data[(bitOffset / 8)..]),
        32 => BinaryPrimitives.ReadUInt32LittleEndian(data[(bitOffset / 8)..]),
        64 => BinaryPrimitives.ReadUInt64LittleEndian(data[(bitOffset / 8)..]),
        _ => throw new ArgumentOutOfRangeException(nameof(width))
    };

    internal static void CheckScalar(int bitOffset, int width)
    {
        if (width is not (1 or 8 or 16 or 32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (bitOffset < 0 || (bitOffset & (width - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitOffset));
        }
    }

    internal static void CheckIndex(int index, int count)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    internal static InvalidDataException Invalid(string message) => new(message);
}
