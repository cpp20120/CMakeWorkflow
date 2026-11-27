using System.Buffers.Binary;

namespace Capnp.Managed;

/// <summary>A borrowed struct data prefix and pointer section. Absent fields read as their schema defaults.</summary>
public readonly ref struct StructReader
{
    private readonly MessageReader message;
    private readonly ReadOnlySpan<byte> data;
    private readonly WordAddress pointers;
    private readonly int pointerCount;

    internal StructReader(MessageReader message, StructLocation value)
    {
        this.message = message;
        data = value.Data == 0 ? default : message.Bytes(value.Start, value.Data * 8);
        pointers = value.Start.Add(value.Data);
        pointerCount = value.Pointers;
    }

    internal StructReader(MessageReader message, ReadOnlySpan<byte> data, WordAddress pointers, int pointerCount)
    {
        this.message = message;
        this.data = data;
        this.pointers = pointers;
        this.pointerCount = pointerCount;
    }

    public ulong ReadScalar(int bitOffset, int width, ulong defaultValue = 0)
    {
        Wire.CheckScalar(bitOffset, width);
        return (long)bitOffset + width <= (long)data.Length * 8
            ? Wire.ReadScalar(data, bitOffset, width) ^ defaultValue
            : defaultValue;
    }

    public ulong ReadDataWord(WordOffset offset) =>
        (uint)offset.Value < (uint)(data.Length / 8)
            ? BinaryPrimitives.ReadUInt64LittleEndian(data[(offset.Value * 8)..])
            : 0;

    public PointerReader GetPointer(int fieldIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldIndex);
        return fieldIndex < pointerCount ? new(message, pointers.Add(fieldIndex)) : default;
    }
}
