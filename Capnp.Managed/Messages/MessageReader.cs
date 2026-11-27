using System.Buffers.Binary;

namespace Capnp.Managed;

/// <summary>A checked, synchronous borrow of one immutable message. Keep the underlying storage unchanged.</summary>
public readonly ref struct MessageReader
{
    private readonly ReadOnlySpan<byte> contiguous;
    private readonly int[]? offsets;
    private readonly ReadOnlySpan<ReadOnlyMemory<byte>> segments;
    private readonly MessageLimits? limits;

    public MessageReader(ReadOnlySpan<byte> segment, MessageLimits? limits = null)
    {
        this.limits = limits ?? MessageLimits.Default;
        limits?.Check();
        FrameCodec.CheckSize(segment.Length, 0, this.limits);
        contiguous = segment;
        SegmentCount = 1;
    }

    public MessageReader(ReadOnlySpan<ReadOnlyMemory<byte>> segments, MessageLimits? limits = null)
    {
        this.limits = limits ?? MessageLimits.Default;
        limits?.Check();
        FrameCodec.CheckSegments(segments, this.limits);
        contiguous = segments[0].Span;
        this.segments = segments;
        SegmentCount = segments.Length;
    }

    internal MessageReader(ReadOnlySpan<byte> frame, int firstOffset, int firstSize, int[]? offsets,
        MessageLimits limits)
    {
        contiguous = offsets is null ? frame.Slice(firstOffset, firstSize) : frame;
        this.offsets = offsets;
        this.limits = limits;
        SegmentCount = offsets is null ? 1 : offsets.Length - 1;
    }

    public int SegmentCount { get; }
    public MessageLimits Limits => limits ?? MessageLimits.Default;
    public PointerReader Root => new(this, WordAddress.Root);

    public ReadOnlySpan<byte> GetSegment(SegmentId id)
    {
        if (id.Value >= SegmentCount)
        {
            throw Wire.Invalid("Segment ID is outside the message.");
        }

        int index = (int)id.Value;
        return offsets is not null
            ? contiguous[offsets[index]..offsets[index + 1]]
            : index == 0 ? contiguous : segments[index].Span;
    }

    public ulong ReadWord(WordAddress address)
    {
        ReadOnlySpan<byte> segment = GetSegment(address.Segment);
        CheckRange(segment, address.Offset.Value, 1);
        return BinaryPrimitives.ReadUInt64LittleEndian(segment[(address.Offset.Value * 8)..]);
    }

    internal ReadOnlySpan<byte> Bytes(WordAddress start, int length) =>
        GetSegment(start.Segment).Slice(start.Offset.Value * 8, length);

    internal ResolvedPointer Resolve(WordAddress location) => Resolve(location, ReadWord(location));

    internal ResolvedPointer Resolve(WordAddress location, ulong word) => (word & 3) switch
    {
        _ when word == 0 => default,
        0 or 1 => Near(location, word, 0),
        2 => ResolveFar(word),
        _ => throw Wire.Invalid("Capability and unknown pointers are unsupported.")
    };

    private ResolvedPointer ResolveFar(ulong pointer)
    {
        WordAddress landing = FarAddress(pointer);
        bool isDouble = (pointer & 4) != 0;
        ReadOnlySpan<byte> segment = GetSegment(landing.Segment);
        CheckRange(segment, landing.Offset.Value, isDouble ? 2 : 1);
        ReadOnlySpan<byte> words = segment[(landing.Offset.Value * 8)..];
        ulong firstWord = BinaryPrimitives.ReadUInt64LittleEndian(words);
        if (!isDouble)
        {
            // The landing word supplies the tag, so a zero tag describes an empty struct.
            return (firstWord & 3) <= 1
                ? Near(landing, firstWord, 1)
                : throw Wire.Invalid("Invalid single-far landing pad.");
        }

        ulong tag = BinaryPrimitives.ReadUInt64LittleEndian(words[8..]);
        // A zero tag is the legal double-far tag for a zero-sized struct.
        return (firstWord & 7) == 2 && (tag & 3) <= 1 && Wire.SignedOffset(tag) == 0
            ? new(tag, FarAddress(firstWord), 2)
            : throw Wire.Invalid("Invalid double-far landing pad.");
    }

    internal StructLocation ReadStruct(ResolvedPointer pointer)
    {
        if (pointer is { Tag: 0, LandingWords: 0 })
        {
            return default;
        }

        if ((pointer.Tag & 3) != 0)
        {
            throw Wire.Invalid("Expected a struct pointer.");
        }

        StructLayout layout = Wire.Layout(pointer.Tag);
        CheckRange(GetSegment(pointer.Target.Segment), pointer.Target.Offset.Value, layout.DataWords + layout.PointerWords);
        return new(pointer.Target, layout);
    }

    internal ListLocation ReadList(ResolvedPointer pointer)
    {
        if (pointer is { Tag: 0, LandingWords: 0 })
        {
            return default;
        }

        if ((pointer.Tag & 3) != 1)
        {
            throw Wire.Invalid("Expected a list pointer.");
        }

        ulong tag = pointer.Tag;
        ListElementSize kind = (ListElementSize)((tag >> 32) & 7);
        int count = (int)(tag >> 35);
        ReadOnlySpan<byte> segment = GetSegment(pointer.Target.Segment);
        int start = pointer.Target.Offset.Value;
        if (kind != ListElementSize.Composite)
        {
            int size = Wire.Words(kind, count);
            CheckRange(segment, start, size);
            CheckListLength(count);
            return new(pointer.Target, count, size, kind, default);
        }

        CheckRange(segment, start, count + 1);
        ulong composite = BinaryPrimitives.ReadUInt64LittleEndian(segment[(start * 8)..]);
        int elements = (int)((uint)composite >> 2);
        StructLayout layout = Wire.Layout(composite);
        if ((composite & 3) != 0 || (long)elements * (layout.DataWords + layout.PointerWords) > count)
        {
            throw Wire.Invalid("Invalid inline composite list tag.");
        }

        CheckListLength(elements);
        return new(pointer.Target.Add(1), elements, count, kind, layout);
    }

    private void CheckListLength(int count)
    {
        if (count > Limits.MaxListElements)
        {
            throw Wire.Invalid("List exceeds its element limit.");
        }
    }

    private static ResolvedPointer Near(WordAddress location, ulong word, int landingWords) =>
        new(word, location.Add(1 + Wire.SignedOffset(word)), landingWords);

    private static WordAddress FarAddress(ulong word) => new(new((uint)(word >> 32)), new((int)((uint)word >> 3)));

    private static void CheckRange(ReadOnlySpan<byte> segment, int start, int size)
    {
        int words = segment.Length / 8;
        if ((uint)start > (uint)words || size > words - start)
        {
            throw Wire.Invalid("Pointer or landing pad is outside its segment.");
        }
    }
}
