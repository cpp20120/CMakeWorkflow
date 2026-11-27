using System.Buffers.Binary;

namespace Capnp.Managed;

/// <summary>Unpacked Cap’n Proto stream framing over exactly one complete message.</summary>
public static class FrameCodec
{
    public static MessageReader Read(ReadOnlySpan<byte> frame, MessageLimits? limits = null)
    {
        MessageLimits policy = limits ?? MessageLimits.Default;
        limits?.Check();
        if (frame.Length < 16)
        {
            throw Wire.Invalid("Truncated frame.");
        }

        uint countMinusOne = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (countMinusOne >= policy.MaxSegments)
        {
            throw Wire.Invalid("Frame exceeds its segment count limit.");
        }

        int count = (int)countMinusOne + 1;
        int header = HeaderBytes(count);
        if (header > frame.Length)
        {
            throw Wire.Invalid("Truncated segment table.");
        }

        int firstSize = SegmentBytes(frame, 0, policy);
        if (count == 1)
        {
            return firstSize == frame.Length - header
                ? new(frame, header, firstSize, default, policy)
                : throw Wire.Invalid("Frame has missing or trailing bytes.");
        }

        int end = header;
        // Validate the whole table before allocating the index.
        for (int i = 0; i < count; i++)
        {
            int size = SegmentBytes(frame, i, policy);
            if (size > frame.Length - end || size > policy.MaxMessageBytes - (end - header))
            {
                throw Wire.Invalid("Frame exceeds its total size or is truncated.");
            }

            end += size;
        }

        if (end != frame.Length)
        {
            throw Wire.Invalid("Frame has trailing bytes.");
        }

        int[] offsets = new int[count + 1];
        offsets[0] = header;
        for (int i = 0; i < count; i++)
        {
            offsets[i + 1] = offsets[i] + SegmentBytes(frame, i, policy);
        }

        return new(frame, header, firstSize, offsets, policy);
    }

    /// <summary>Reads one frame at the current position, leaving the stream open for another frame.</summary>
    public static OwnedMessage Read(Stream stream, MessageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MessageLimits policy = limits ?? MessageLimits.Default;
        limits?.Check();
        Span<byte> prefix = stackalloc byte[4];
        ReadExactly(stream, prefix);
        uint countMinusOne = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (countMinusOne >= policy.MaxSegments)
        {
            throw Wire.Invalid("Frame exceeds its segment count limit.");
        }

        int count = (int)countMinusOne + 1;
        byte[] table = new byte[HeaderBytes(count)];
        prefix.CopyTo(table);
        ReadExactly(stream, table.AsSpan(4));
        int remaining = policy.MaxMessageBytes;
        for (int i = 0; i < count; i++)
        {
            int size = SegmentBytes(table, i, policy);
            if (size > remaining)
            {
                throw Wire.Invalid("Frame exceeds its total size limit.");
            }

            remaining -= size;
        }

        long length = (long)table.Length + policy.MaxMessageBytes - remaining;
        if (length > int.MaxValue)
        {
            throw Wire.Invalid("Frame size overflows the managed address range.");
        }

        byte[] frame = new byte[(int)length];
        table.CopyTo(frame, 0);
        ReadExactly(stream, frame.AsSpan(table.Length));
        return OwnedMessage.FromFrame(frame, policy);
    }

    private static void ReadExactly(Stream stream, Span<byte> output)
    {
        try
        {
            stream.ReadExactly(output);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("Truncated frame.", exception);
        }
    }

    public static byte[] Write(MessageReader message)
    {
        MessageLimits policy = message.Limits;
        if (message.SegmentCount == 0)
        {
            throw Wire.Invalid("Message has no root segment.");
        }

        int header = HeaderBytes(message.SegmentCount);
        long length = header;
        for (uint i = 0; i < message.SegmentCount; i++)
        {
            length += message.GetSegment(new(i)).Length;
        }

        if (length > policy.MaxFrameBytes)
        {
            throw Wire.Invalid("Frame exceeds the output size limit.");
        }

        byte[] frame = new byte[(int)length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, message.SegmentCount - 1);
        int offset = header;
        for (int i = 0; i < message.SegmentCount; i++)
        {
            ReadOnlySpan<byte> segment = message.GetSegment(new((uint)i));
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan((i + 1) * 4), segment.Length / 8);
            segment.CopyTo(frame.AsSpan(offset));
            offset += segment.Length;
        }

        return frame;
    }

    internal static int HeaderBytes(int count) => ((count + 2) & ~1) * 4;

    internal static void CheckSegments(ReadOnlySpan<ReadOnlyMemory<byte>> segments, MessageLimits limits)
    {
        if (segments.IsEmpty || segments.Length > limits.MaxSegments)
        {
            throw Wire.Invalid("Invalid segment count.");
        }

        int remaining = limits.MaxMessageBytes;
        for (int i = 0; i < segments.Length; i++)
        {
            int length = segments[i].Length;
            CheckSize(length, i, limits);
            remaining -= length;
            if (remaining < 0)
            {
                throw Wire.Invalid("Message exceeds its total size limit.");
            }
        }
    }

    internal static void CheckSize(int length, int index, MessageLimits policy)
    {
        if ((length & 7) != 0 || (index == 0 && length < 8) || length > policy.MaxSegmentBytes
            || length > policy.MaxMessageBytes)
        {
            throw Wire.Invalid("Invalid segment size.");
        }
    }

    private static int SegmentBytes(ReadOnlySpan<byte> frame, int index, MessageLimits policy)
    {
        long size = BinaryPrimitives.ReadUInt32LittleEndian(frame[((index + 1) * 4)..]) * 8L;
        if (size > int.MaxValue)
        {
            throw Wire.Invalid("Segment size overflows the managed address range.");
        }

        CheckSize((int)size, index, policy);
        return (int)size;
    }
}
