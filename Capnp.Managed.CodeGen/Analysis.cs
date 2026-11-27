using System.Globalization;
using Capnp.Managed.Schema;

namespace Capnp.Managed.CodeGen;

internal sealed class Analysis
{
    private readonly SchemaRequest request;
    private readonly Dictionary<ulong, Dictionary<int, ulong>> occupiedSlots = [];
    internal Analysis(SchemaRequest request)
    {
        this.request = request;
    }

    internal SchemaNode Resolve(ulong id) => request.Nodes.TryGetValue(id, out SchemaNode? node)
        ? node : throw new InvalidDataException($"Unresolved schema node 0x{id:x16}.");

    internal static string Name(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 180)
        {
            throw new InvalidDataException("Invalid or excessive C# identifier.");
        }

        string result = string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
        return char.IsDigit(result[0]) ? "_" + result : char.ToUpperInvariant(result[0]) + result[1..];
    }

    internal static string Namespace(SchemaNode file)
    {
        string stem = Path.GetFileNameWithoutExtension(file.DisplayName.Replace('\\', '/'));
        return "Capnp.Generated." + Name(stem);
    }

    internal string Qualified(ulong id)
    {
        SchemaNode node = Resolve(id);
        SchemaNode file = File(node);
        return $"global::{Namespace(file)}.{Local(node)}";
    }

    internal string Local(SchemaNode node)
    {
        List<string> names = [];
        while (node.Kind != SchemaKind.File)
        {
            if (names.Count == 24)
            {
                throw new InvalidDataException("Schema scope cycle or nesting beyond 24.");
            }

            names.Add(Name(node.Name));
            node = Resolve(node.Scope);
        }

        names.Reverse();
        return string.Join('_', names);
    }

    internal SchemaNode File(SchemaNode node)
    {
        for (int depth = 0; depth < 24; depth++)
        {
            if (node.Kind == SchemaKind.File)
            {
                return node;
            }

            node = Resolve(node.Scope);
        }

        throw new InvalidDataException("Schema scope cycle or nesting beyond 24.");
    }

    internal IEnumerable<SchemaNode> Declarations(ulong file) => request.Nodes.Values
        .Where(node => node.Kind != SchemaKind.File && File(node).Id == file)
        .OrderBy(Local, StringComparer.Ordinal).ThenBy(node => node.Id);

    internal void Check()
    {
        HashSet<string> namespaces = new(StringComparer.OrdinalIgnoreCase);
        foreach (SchemaNode file in request.Nodes.Values.Where(node => node.Kind == SchemaKind.File))
        {
            Claim(namespaces, Namespace(file), file.DisplayName);
        }

        HashSet<ulong> requested = [];
        foreach (RequestedFile file in request.Files)
        {
            if (!requested.Add(file.Id) || Resolve(file.Id).Kind != SchemaKind.File)
            {
                throw new InvalidDataException($"Invalid requested file '{file.Name}'.");
            }

            foreach (NestedDeclaration import in file.Imports)
            {
                // The frontend can fold imported constants without emitting the imported file node.
                if (import.Id == 0 || (request.Nodes.TryGetValue(import.Id, out SchemaNode? imported) && imported.Kind != SchemaKind.File))
                {
                    throw new InvalidDataException($"Invalid import '{import.Name}' in '{file.Name}'.");
                }
            }

            HashSet<string> names = new(StringComparer.Ordinal)
            {
                "MessageReader", "MessageBuilder", "StructReader", "StructBuilder", "ListReader", "ListBuilder",
                "PointerReader", "StructLayout", "ListElementSize", "ReadOnlySpan", "Convert", "BitConverter",
                "InvalidOperationException", "System", "Capnp"
            };
            SchemaNode[] declarations = [.. Declarations(file.Id)];
            foreach (SchemaNode node in declarations)
            {
                CheckNode(node);
                if (node.Kind != SchemaKind.Annotation)
                {
                    Claim(names, Local(node), node.DisplayName);
                }
            }

            foreach (SchemaNode declaration in declarations.Where(node => node.Kind == SchemaKind.Struct))
            {
                _ = Occupied(declaration);
            }
        }
    }

    private void CheckNode(SchemaNode node)
    {
        if (node.IsGeneric || node.Kind == SchemaKind.Interface)
        {
            throw Unsupported(node, "generic declarations, interfaces and capabilities");
        }

        foreach (NestedDeclaration nested in node.Nested)
        {
            if (Resolve(nested.Id).Scope != node.Id)
            {
                throw new InvalidDataException($"Invalid nested declaration '{nested.Name}' in '{node.DisplayName}'.");
            }
        }

        if (node.Kind == SchemaKind.Constant && node.ConstantType is not null)
        {
            CheckType(node.ConstantType, node);
        }

        if (node.Kind != SchemaKind.Struct)
        {
            return;
        }

        if (node.DiscriminantCount != 0 && ((ulong)node.DiscriminantOffset + 1) * 16 > (ulong)node.DataWords * 64)
        {
            throw new InvalidDataException($"Union tag is outside '{node.DisplayName}'.");
        }

        HashSet<ushort> discriminants = [];
        HashSet<ushort> ordinals = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (SchemaField field in node.Fields)
        {
            Claim(names, Name(field.Name), node.DisplayName);
            if (field.Ordinal is ushort ordinal && !ordinals.Add(ordinal))
            {
                throw new InvalidDataException($"Duplicate field ordinal in '{node.DisplayName}'.");
            }

            if (field.Discriminant != ushort.MaxValue && (field.Discriminant >= node.DiscriminantCount
                || !discriminants.Add(field.Discriminant)))
            {
                throw new InvalidDataException($"Invalid discriminant in '{node.DisplayName}'.");
            }

            if (field.Group != 0)
            {
                SchemaNode group = Resolve(field.Group);
                if (!group.IsGroup || group.Scope != node.Id || group.DataWords != node.DataWords
                    || group.PointerWords != node.PointerWords)
                {
                    throw new InvalidDataException($"Invalid group '{field.Name}' in '{node.DisplayName}'.");
                }
            }
            else
            {
                CheckType(field.Type, node);
                int width = Width(field.Type.Kind);
                if ((width > 0 && (((ulong)field.Offset + 1) * (uint)width) > ((ulong)node.DataWords * 64))
                    || (width < 0 && field.Offset >= node.PointerWords))
                {
                    throw new InvalidDataException($"Field '{field.Name}' is outside '{node.DisplayName}'.");
                }
            }
        }

        if (discriminants.Count != node.DiscriminantCount)
        {
            throw new InvalidDataException($"Incomplete union metadata for '{node.DisplayName}'.");
        }
    }

    private Dictionary<int, ulong> Occupied(SchemaNode node)
    {
        if (occupiedSlots.TryGetValue(node.Id, out Dictionary<int, ulong>? existing))
        {
            return existing;
        }

        Dictionary<int, ulong> common = [];
        Dictionary<int, ulong> union = [];
        if (node.DiscriminantCount > 0)
        {
            (int word, ulong mask) = DataMask((int)node.DiscriminantOffset * 16, 16);
            common.Add(word, mask);
        }

        foreach (SchemaField field in node.Fields)
        {
            bool variant = field.Discriminant != ushort.MaxValue;
            if (field.Group != 0)
            {
                foreach ((int word, ulong mask) in Occupied(Resolve(field.Group)))
                {
                    ClaimSlot(word, mask, variant, common, union, node.DisplayName);
                }

                continue;
            }

            int width = Width(field.Type.Kind);
            if (width == 0)
            {
                continue;
            }

            (int wordIndex, ulong fieldMask) = width < 0
                ? (-1 - (int)field.Offset, ulong.MaxValue)
                : DataMask((int)field.Offset * width, width);
            ClaimSlot(wordIndex, fieldMask, variant, common, union, node.DisplayName);
        }

        foreach ((int word, ulong mask) in union)
        {
            common[word] = common.GetValueOrDefault(word) | mask;
        }

        occupiedSlots.Add(node.Id, common);
        return common;
    }

    private static (int Word, ulong Mask) DataMask(int offset, int width) =>
        (offset / 64, (width == 64 ? ulong.MaxValue : (1UL << width) - 1) << (offset & 63));

    private static void ClaimSlot(int word, ulong mask, bool variant, Dictionary<int, ulong> common,
        Dictionary<int, ulong> union, string scope)
    {
        ulong commonMask = common.GetValueOrDefault(word);
        ulong unionMask = union.GetValueOrDefault(word);
        if ((commonMask & mask) != 0 || (!variant && (unionMask & mask) != 0))
        {
            throw new InvalidDataException($"Overlapping fields or union discriminant in '{scope}'.");
        }

        if (variant)
        {
            union[word] = unionMask | mask;
        }
        else
        {
            common[word] = commonMask | mask;
        }
    }

    private void CheckType(SchemaType type, SchemaNode owner)
    {
        if (type.Branded || type.Kind == ValueKind.Interface)
        {
            throw Unsupported(owner, "brands, constrained or parameter AnyPointer, interfaces and capabilities");
        }

        if (type.Kind is ValueKind.Struct or ValueKind.Enum)
        {
            SchemaNode target = Resolve(type.Target);
            if (target.IsGeneric || target.IsGroup || target.Kind != (type.Kind == ValueKind.Struct ? SchemaKind.Struct : SchemaKind.Enum))
            {
                throw Unsupported(owner, $"invalid or unsupported type '{target.DisplayName}'");
            }
        }
        else if (type.Kind == ValueKind.List)
        {
            CheckType(type.Element ?? throw new InvalidDataException("List has no element type."), owner);
        }
    }

    internal static void Claim(HashSet<string> names, string name, string scope)
    {
        if (!names.Add(name))
        {
            throw new InvalidDataException($"C# naming collision for '{name}' in '{scope}'.");
        }
    }

    internal static InvalidDataException Unsupported(SchemaNode node, string reason) =>
        new($"Unsupported schema in '{node.DisplayName}' at byte {node.StartByte.ToString(CultureInfo.InvariantCulture)}: {reason}.");

    internal static int Width(ValueKind kind) => kind switch
    {
        ValueKind.Void => 0,
        ValueKind.Bool => 1,
        ValueKind.SignedByte or ValueKind.UnsignedByte => 8,
        ValueKind.SignedShort or ValueKind.UnsignedShort or ValueKind.Enum => 16,
        ValueKind.SignedWord or ValueKind.UnsignedWord or ValueKind.SinglePrecision => 32,
        ValueKind.SignedLong or ValueKind.UnsignedLong or ValueKind.DoublePrecision => 64,
        ValueKind.Text or ValueKind.Data or ValueKind.List or ValueKind.Struct or ValueKind.AnyPointer => -1,
        ValueKind.Interface => throw new InvalidDataException("Capability types are unsupported."),
        _ => throw new InvalidDataException("Unknown field type.")
    };
}
