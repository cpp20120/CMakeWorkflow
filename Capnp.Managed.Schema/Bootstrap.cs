using System.Collections.Immutable;

namespace Capnp.Managed.Schema;

/// <summary>Reads the official Cap’n Proto 1.2.0 CodeGeneratorRequest bootstrap subset.</summary>
public static class Bootstrap
{
    private static readonly MessageLimits limits = MessageLimits.Default with
    {
        MaxMessageBytes = 16 * 1024 * 1024,
        MaxSegmentBytes = 16 * 1024 * 1024,
        MaxListElements = 65_536,
        MaxTextBytes = 16_384,
        MaxTraversalWords = 2 * 1024 * 1024
    };

    public static SchemaRequest Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        OwnedMessage message = FrameCodec.Read(stream, limits);
        return stream.ReadByte() == -1
            ? message.Inspect(Parse)
            : throw new InvalidDataException("Trailing bytes after CodeGeneratorRequest.");
    }

    private static SchemaRequest Parse(MessageReader message)
    {
        CanonicalMessage.Validate(message);
        StructReader root = message.Root.AsStruct();
        StructReader version = root.GetPointer(2).AsStruct();
        if (version.ReadScalar(0, 16) != 1 || version.ReadScalar(16, 8) != 2 || version.ReadScalar(24, 8) != 0)
        {
            throw new InvalidDataException("Expected compiler protocol 1.2.0.");
        }

        ImmutableDictionary<ulong, SchemaNode>.Builder nodes = ImmutableDictionary.CreateBuilder<ulong, SchemaNode>();
        ListReader declarations = Structs(root, 0);
        if (declarations.Count > 8192)
        {
            throw new InvalidDataException("CodeGeneratorRequest exceeds the schema node limit.");
        }

        for (int i = 0; i < declarations.Count; i++)
        {
            SchemaNode node = Node(declarations.GetStruct(i));
            if (node.Id == 0 || !nodes.TryAdd(node.Id, node))
            {
                throw new InvalidDataException("Missing or duplicate schema node ID.");
            }
        }

        ListReader requests = Structs(root, 1);
        if (requests.Count is 0 or > 128)
        {
            throw new InvalidDataException("CodeGeneratorRequest requires 1 through 128 requested files.");
        }

        ImmutableArray<RequestedFile>.Builder files = ImmutableArray.CreateBuilder<RequestedFile>(requests.Count);
        for (int i = 0; i < requests.Count; i++)
        {
            StructReader file = requests.GetStruct(i);
            ListReader imports = Structs(file, 1);
            ImmutableArray<NestedDeclaration>.Builder dependencies = ImmutableArray.CreateBuilder<NestedDeclaration>(imports.Count);
            for (int j = 0; j < imports.Count; j++)
            {
                StructReader import = imports.GetStruct(j);
                dependencies.Add(new(Text(import, 0), import.ReadScalar(0, 64)));
            }

            files.Add(new(file.ReadScalar(0, 64), Text(file, 0), dependencies.MoveToImmutable()));
        }

        return new(nodes.ToImmutable(), files.MoveToImmutable());
    }

    private static SchemaNode Node(StructReader node)
    {
        ulong id = node.ReadScalar(0, 64);
        string displayName = Text(node, 0);
        ulong prefix = node.ReadScalar(64, 32);
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(displayName);
        if (prefix > (ulong)utf8.Length)
        {
            throw new InvalidDataException("Invalid schema display name prefix.");
        }

        string name = System.Text.Encoding.UTF8.GetString(utf8.AsSpan((int)prefix));
        SchemaKind kind = (SchemaKind)node.ReadScalar(96, 16);
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException("Unknown schema node kind.");
        }

        ImmutableArray<SchemaField>.Builder fields = ImmutableArray.CreateBuilder<SchemaField>();
        ImmutableArray<string>.Builder enumerants = ImmutableArray.CreateBuilder<string>();
        ImmutableArray<NestedDeclaration>.Builder nested = ImmutableArray.CreateBuilder<NestedDeclaration>();
        ListReader children = Structs(node, 1);
        for (int i = 0; i < children.Count; i++)
        {
            StructReader child = children.GetStruct(i);
            nested.Add(new(Text(child, 0), child.ReadScalar(0, 64)));
        }

        SchemaType? constantType = null;
        SchemaValue? constantValue = null;
        if (kind == SchemaKind.Struct)
        {
            ListReader values = Structs(node, 3);
            if (values.Count > 4096)
            {
                throw new InvalidDataException("Schema declaration exceeds the member limit.");
            }

            for (int i = 0; i < values.Count; i++)
            {
                fields.Add(Field(values.GetStruct(i)));
            }
        }
        else if (kind == SchemaKind.Enum)
        {
            ListReader values = Structs(node, 3);
            if (values.Count > 4096)
            {
                throw new InvalidDataException("Schema declaration exceeds the member limit.");
            }

            for (int i = 0; i < values.Count; i++)
            {
                enumerants.Add(Text(values.GetStruct(i), 0));
            }
        }
        else if (kind == SchemaKind.Constant)
        {
            constantType = Type(node.GetPointer(3).AsStruct(), 0);
            constantValue = Value(node.GetPointer(4).AsStruct(), constantType.Kind);
        }

        return new(id, node.ReadScalar(128, 64), name, displayName, kind,
            (ushort)node.ReadScalar(112, 16), (ushort)node.ReadScalar(192, 16),
            kind == SchemaKind.Struct && node.ReadScalar(224, 1) != 0, node.ReadScalar(288, 1) != 0,
            (ushort)node.ReadScalar(240, 16), (uint)node.ReadScalar(256, 32),
            fields.ToImmutable(), enumerants.ToImmutable(), nested.ToImmutable(), constantType, constantValue,
            Annotations(node, 2), (uint)node.ReadScalar(320, 32), (uint)node.ReadScalar(352, 32));
    }

    private static SchemaField Field(StructReader field)
    {
        string name = Text(field, 0);
        ushort discriminant = (ushort)field.ReadScalar(16, 16, ushort.MaxValue);
        ushort? ordinal = field.ReadScalar(80, 16) switch
        {
            0 => null,
            1 => (ushort)field.ReadScalar(96, 16),
            _ => throw new InvalidDataException("Invalid field ordinal.")
        };
        ImmutableArray<ulong> annotations = Annotations(field, 1);
        ulong kind = field.ReadScalar(64, 16);
        if (kind == 1)
        {
            ulong group = field.ReadScalar(128, 64);
            return new(name, 0, discriminant, ordinal, new(ValueKind.Void), new(0, null),
                group == 0 ? throw new InvalidDataException("Group has no node ID.") : group, annotations);
        }

        if (kind != 0 || field.GetPointer(2).IsNull || field.GetPointer(3).IsNull)
        {
            throw new InvalidDataException("Invalid field slot metadata.");
        }

        SchemaType type = Type(field.GetPointer(2).AsStruct(), 0);
        return new(name, (uint)field.ReadScalar(32, 32), discriminant, ordinal, type,
            Value(field.GetPointer(3).AsStruct(), type.Kind), 0, annotations);
    }

    private static SchemaType Type(StructReader type, int depth)
    {
        if (depth >= 24)
        {
            throw new InvalidDataException("Schema type exceeds the nesting limit.");
        }

        ValueKind kind = (ValueKind)type.ReadScalar(0, 16);
        return kind switch
        {
            ValueKind.List => new(kind, Element: Type(type.GetPointer(0).AsStruct(), depth + 1)),
            ValueKind.Enum or ValueKind.Struct or ValueKind.Interface => new(kind, type.ReadScalar(64, 64),
                Branded: Structs(type.GetPointer(0).AsStruct(), 0).Count > 0),
            ValueKind.AnyPointer => new(kind, Branded: type.ReadScalar(64, 32) != 0),
            ValueKind.Void or ValueKind.Bool or ValueKind.SignedByte or ValueKind.SignedShort or ValueKind.SignedWord
                or ValueKind.SignedLong or ValueKind.UnsignedByte or ValueKind.UnsignedShort or ValueKind.UnsignedWord
                or ValueKind.UnsignedLong or ValueKind.SinglePrecision or ValueKind.DoublePrecision or ValueKind.Text
                or ValueKind.Data => new(kind),
            _ => throw new InvalidDataException("Unknown compiler type kind.")
        };
    }

    private static SchemaValue Value(StructReader value, ValueKind expected)
    {
        if (value.ReadScalar(0, 16) != (ushort)expected)
        {
            throw new InvalidDataException("Default value type differs from the field or constant type.");
        }

        ulong bits = expected switch
        {
            ValueKind.Bool => value.ReadScalar(16, 1),
            ValueKind.SignedByte or ValueKind.UnsignedByte => value.ReadScalar(16, 8),
            ValueKind.SignedShort or ValueKind.UnsignedShort or ValueKind.Enum => value.ReadScalar(16, 16),
            ValueKind.SignedWord or ValueKind.UnsignedWord or ValueKind.SinglePrecision => value.ReadScalar(32, 32),
            ValueKind.SignedLong or ValueKind.UnsignedLong or ValueKind.DoublePrecision => value.ReadScalar(64, 64),
            ValueKind.Void or ValueKind.Text or ValueKind.Data or ValueKind.List or ValueKind.Struct
                or ValueKind.Interface or ValueKind.AnyPointer => 0,
            _ => throw new InvalidDataException("Unknown default value type.")
        };
        PointerReader pointer = value.GetPointer(0);
        if (pointer.IsNull)
        {
            return new(bits, null);
        }

        switch (expected)
        {
            case ValueKind.Text: _ = pointer.AsText(); break;
            case ValueKind.Data: _ = pointer.AsData(); break;
            case ValueKind.List: _ = pointer.AsList(); break;
            case ValueKind.Struct: _ = pointer.AsStruct(); break;
            case ValueKind.AnyPointer: break;
            case ValueKind.Void or ValueKind.Bool or ValueKind.SignedByte or ValueKind.SignedShort or ValueKind.SignedWord
                or ValueKind.SignedLong or ValueKind.UnsignedByte or ValueKind.UnsignedShort or ValueKind.UnsignedWord
                or ValueKind.UnsignedLong or ValueKind.SinglePrecision or ValueKind.DoublePrecision or ValueKind.Enum
                or ValueKind.Interface:
                throw new InvalidDataException("Scalar default unexpectedly contains a pointer.");
            default:
                throw new InvalidDataException("Unknown default kind.");
        }

        return new(bits, Convert.ToHexString(CanonicalMessage.CreatePointerSegment(pointer)));
    }

    private static ListReader Structs(StructReader value, int slot) => value.GetPointer(slot).AsList(ListElementSize.Composite);
    private static string Text(StructReader value, int slot) =>
        value.GetPointer(slot).DecodeText() ?? throw new InvalidDataException("Missing schema name.");

    private static ImmutableArray<ulong> Annotations(StructReader value, int slot)
    {
        ListReader list = Structs(value, slot);
        ImmutableArray<ulong>.Builder ids = ImmutableArray.CreateBuilder<ulong>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            ids.Add(list.GetStruct(i).ReadScalar(0, 64));
        }

        return ids.MoveToImmutable();
    }
}
