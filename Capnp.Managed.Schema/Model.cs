using System.Collections.Immutable;

namespace Capnp.Managed.Schema;

public enum SchemaKind { File, Struct, Enum, Interface, Constant, Annotation }
public enum ValueKind
{
    Void, Bool, SignedByte, SignedShort, SignedWord, SignedLong, UnsignedByte, UnsignedShort, UnsignedWord, UnsignedLong,
    SinglePrecision, DoublePrecision, Text, Data, List, Enum, Struct, Interface, AnyPointer
}

public sealed record SchemaType(ValueKind Kind, ulong Target = 0, SchemaType? Element = null, bool Branded = false);
public sealed record SchemaValue(ulong Bits, string? PointerHex);
public sealed record SchemaField(string Name, uint Offset, ushort Discriminant, ushort? Ordinal,
    SchemaType Type, SchemaValue Default, ulong Group, ImmutableArray<ulong> Annotations);
public sealed record NestedDeclaration(string Name, ulong Id);
public sealed record SchemaNode(ulong Id, ulong Scope, string Name, string DisplayName, SchemaKind Kind,
    ushort DataWords, ushort PointerWords, bool IsGroup, bool IsGeneric, ushort DiscriminantCount, uint DiscriminantOffset,
    ImmutableArray<SchemaField> Fields, ImmutableArray<string> Enumerants, ImmutableArray<NestedDeclaration> Nested,
    SchemaType? ConstantType, SchemaValue? ConstantValue, ImmutableArray<ulong> Annotations, uint StartByte, uint EndByte);
public sealed record RequestedFile(ulong Id, string Name, ImmutableArray<NestedDeclaration> Imports);
public sealed record SchemaRequest(ImmutableDictionary<ulong, SchemaNode> Nodes, ImmutableArray<RequestedFile> Files);
