using System.Buffers.Binary;

namespace Capnp.Managed;

internal sealed class WordArena
{
    private byte[] bytes;
    private readonly int maxBytes;
    private int words = 1; // Root pointer.

    internal WordArena(int maxBytes, int initialCapacityBytes = 256)
    {
        this.maxBytes = maxBytes;
        bytes = new byte[Math.Min(Math.Max(256, initialCapacityBytes), maxBytes)];
    }

    internal byte[] Finish() => bytes.AsSpan(0, words * 8).ToArray();

    internal int Allocate(int count)
    {
        if (count < 0 || count > (maxBytes / 8) - words)
        {
            throw Wire.Invalid("Output segment exceeds its size limit.");
        }

        int start = words;
        words += count;
        if (words * 8 > bytes.Length)
        {
            Array.Resize(ref bytes, (int)Math.Min(maxBytes, Math.Max(words * 8L, bytes.Length * 2L)));
        }

        return start;
    }

    internal void Write(int word, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(Slice(word, 8), value);

    internal Span<byte> Slice(int word, int length) => bytes.AsSpan(word * 8, length);

    internal StructLocation Struct(int pointer, StructLayout layout)
    {
        int start = Allocate(layout.DataWords + layout.PointerWords);
        Write(pointer, (layout.DataWords + layout.PointerWords == 0 ? Offset(-1) : Offset(start - pointer - 1))
            | Wire.EncodeLayout(layout));
        return new(new(default, new(start)), layout);
    }

    internal int List(int pointer, ListElementSize kind, int count, int size)
    {
        int start = Allocate(size);
        Write(pointer, 1 | Offset(start - pointer - 1) | ((ulong)kind << 32) | ((ulong)count << 35));
        return start;
    }

    internal ListLocation Composite(int pointer, int count, StructLayout layout)
    {
        long size = (long)count * (layout.DataWords + layout.PointerWords);
        if (size >= maxBytes / 8 || count > 0x3fffffff || size > 0x1fffffff)
        {
            throw Wire.Invalid("Composite list exceeds the output segment size limit.");
        }

        int start = List(pointer, ListElementSize.Composite, (int)size, (int)size + 1);
        Write(start, Offset(count) | Wire.EncodeLayout(layout));
        return new(new(default, new(start + 1)), count, (int)size, ListElementSize.Composite, layout);
    }

    internal void PublishPointer(int temporary, int destination)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(Slice(temporary, 8));
        if (value is not (0 or 0xfffffffc))
        {
            int target = temporary + 1 + Wire.SignedOffset(value);
            value = (value & 0xffffffff00000003) | Offset(target - destination - 1);
        }

        Write(destination, value);
    }

    private static uint Offset(int offset) => Wire.EncodeOffset(offset);
}
