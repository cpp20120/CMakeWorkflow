using System.Security.Cryptography;

namespace Capnp.Managed;

/// <summary>Canonicalizes reachable wire content independently of schema and segment placement.</summary>
public static class CanonicalMessage
{
    public static byte[] CreateSegment(MessageReader message)
    {
        MessageLimits limits = message.Limits;
        WordArena arena = new(Math.Min(limits.MaxCanonicalBytes, Math.Min(limits.MaxMessageBytes, limits.MaxSegmentBytes)));
        Walk(message, arena);
        return arena.Finish();
    }

    /// <summary>Copies the pointed-to value into a canonical segment whose root is that pointer.</summary>
    public static byte[] CreatePointerSegment(PointerReader value)
    {
        MessageLimits limits = value.Message.Limits;
        WordArena arena = new(Math.Min(limits.MaxCanonicalBytes, Math.Min(limits.MaxMessageBytes, limits.MaxSegmentBytes)));
        CopyTo(value, arena, 0);
        return arena.Finish();
    }

    internal static void CopyTo(PointerReader pointer, WordArena arena, int destination)
    {
        if (pointer.IsNull)
        {
            arena.Write(destination, 0);
            return;
        }

        MessageReader source = pointer.Message;
        int budget = source.Limits.MaxTraversalWords;
        CopyPointer(in source, pointer.Address, arena, destination, 0, ref budget);
    }

    public static void Validate(MessageReader message) => Walk(message, null);

    /// <summary>Hashes an already-canonical unframed segment. The caller establishes canonicality.</summary>
    public static string HashSegment(ReadOnlySpan<byte> segment) => HashExactBytes(segment);

    public static string HashExactBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Walk(MessageReader message, WordArena? arena)
    {
        int budget = message.Limits.MaxTraversalWords;
        CopyPointer(message, WordAddress.Root, arena, 0, 0, ref budget);
    }

    private static void CopyPointer(in MessageReader source, WordAddress pointer, WordArena? arena, int target, int depth,
        ref int budget)
    {
        ResolvedPointer resolved = source.Resolve(pointer);
        if (resolved is { Tag: 0, LandingWords: 0 })
        {
            return;
        }

        Charge(1 + resolved.LandingWords, ref budget);
        if (depth >= source.Limits.MaxNesting)
        {
            throw Wire.Invalid("Cap’n Proto nesting limit exceeded.");
        }

        if ((resolved.Tag & 3) == 0)
        {
            StructLocation value = source.ReadStruct(resolved);
            Charge(value.Data + value.Pointers, ref budget);
            StructLayout trimmed = arena is null ? value.Layout : Trim(source, value);
            StructLocation output = arena?.Struct(target, trimmed) ?? default;
            CopyStruct(source, value, trimmed.PointerWords, arena, output, depth, ref budget);
        }
        else
        {
            CopyList(source, source.ReadList(resolved), arena, target, depth, ref budget);
        }
    }

    private static void CopyList(in MessageReader source, ListLocation list, WordArena? arena, int target, int depth,
        ref int budget)
    {
        Charge(list.Kind is ListElementSize.Void or ListElementSize.Composite
            ? Math.Max(list.Count, list.Words)
            : list.Words, ref budget);
        if (list.Kind == ListElementSize.Composite)
        {
            CopyComposite(source, list, arena, target, depth, ref budget);
            return;
        }

        int start = arena?.List(target, list.Kind, list.Count, list.Words) ?? 0;
        if (list.Kind == ListElementSize.PointerWord)
        {
            for (int i = 0; i < list.Count; i++)
            {
                CopyPointer(source, list.Start.Add(i), arena, start + i, depth + 1, ref budget);
            }
        }
        else if (arena is not null && list.Words > 0)
        {
            long bits = (long)Wire.Bits(list.Kind) * list.Count;
            int length = (int)((bits + 7) / 8);
            Span<byte> output = arena.Slice(start, length);
            source.Bytes(list.Start, length).CopyTo(output);
            if ((bits & 7) != 0)
            {
                output[^1] &= (byte)((1 << (int)(bits & 7)) - 1);
            }
        }
    }

    private static void CopyComposite(in MessageReader source, ListLocation list, WordArena? arena, int target, int depth,
        ref int budget)
    {
        ListLocation output = default;
        if (arena is not null)
        {
            ushort data = 0;
            ushort pointers = 0;
            if (list.Layout != default)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    StructLayout item = Trim(source, list.Item(i));
                    data = Math.Max(data, item.DataWords);
                    pointers = Math.Max(pointers, item.PointerWords);
                }
            }

            output = arena.Composite(target, list.Count, new(data, pointers));
        }

        StructLayout layout = arena is null ? list.Layout : output.Layout;
        if (layout == default)
        {
            return;
        }

        for (int i = 0; i < list.Count; i++)
        {
            StructLocation destination = arena is null ? default : output.Item(i);
            CopyStruct(source, list.Item(i), layout.PointerWords, arena, destination, depth, ref budget);
        }
    }

    private static StructLayout Trim(in MessageReader source, StructLocation value)
    {
        ushort data = value.Layout.DataWords;
        ushort pointers = value.Layout.PointerWords;
        while (data > 0 && source.ReadWord(value.Start.Add(data - 1)) == 0)
        {
            data--;
        }

        while (pointers > 0 && source.ReadWord(value.Pointer(pointers - 1)) == 0)
        {
            pointers--;
        }

        return new(data, pointers);
    }

    private static void CopyStruct(in MessageReader source, StructLocation value, int pointers, WordArena? arena,
        StructLocation output, int depth, ref int budget)
    {
        if (arena is not null && output.Data > 0)
        {
            source.Bytes(value.Start, output.Data * 8).CopyTo(arena.Slice(output.Start.Offset.Value, output.Data * 8));
        }

        for (int i = 0; i < pointers; i++)
        {
            CopyPointer(source, value.Pointer(i), arena, output.Pointer(i).Offset.Value, depth + 1, ref budget);
        }
    }

    private static void Charge(int amount, ref int budget)
    {
        if (amount > budget)
        {
            throw Wire.Invalid("Cap’n Proto traversal limit exceeded.");
        }

        budget -= amount;
    }
}
