using Capnp.Managed.Schema;

namespace Capnp.Managed.CodeGen;

internal sealed partial class Emitter
{
    private string TypeName(SchemaType type) => type.Kind switch
    {
        ValueKind.Bool => "bool",
        ValueKind.SignedByte => "sbyte",
        ValueKind.SignedShort => "short",
        ValueKind.SignedWord => "int",
        ValueKind.SignedLong => "long",
        ValueKind.UnsignedByte => "byte",
        ValueKind.UnsignedShort => "ushort",
        ValueKind.UnsignedWord => "uint",
        ValueKind.UnsignedLong => "ulong",
        ValueKind.SinglePrecision => "float",
        ValueKind.DoublePrecision => "double",
        ValueKind.Enum => analysis.Qualified(type.Target),
        ValueKind.Void or ValueKind.Text or ValueKind.Data or ValueKind.Struct or ValueKind.List
            or ValueKind.Interface or ValueKind.AnyPointer => throw new InvalidDataException("Expected scalar type."),
        _ => throw new InvalidDataException("Unknown scalar type.")
    };

    private string FromBits(SchemaType type, string expression) => type.Kind switch
    {
        ValueKind.Bool => $"{expression} != 0",
        ValueKind.SinglePrecision => $"BitConverter.UInt32BitsToSingle((uint){expression})",
        ValueKind.DoublePrecision => $"BitConverter.UInt64BitsToDouble({expression})",
        ValueKind.SignedByte or ValueKind.SignedShort or ValueKind.SignedWord or ValueKind.SignedLong
            or ValueKind.UnsignedByte or ValueKind.UnsignedShort or ValueKind.UnsignedWord or ValueKind.UnsignedLong
            or ValueKind.Enum => $"unchecked(({TypeName(type)}){expression})",
        ValueKind.Void or ValueKind.Text or ValueKind.Data or ValueKind.Struct or ValueKind.List
            or ValueKind.Interface or ValueKind.AnyPointer => throw new InvalidDataException("Expected scalar type."),
        _ => throw new InvalidDataException("Unknown scalar type.")
    };

    private static string ToBits(SchemaType type) => type.Kind switch
    {
        ValueKind.Bool => "value ? 1UL : 0UL",
        ValueKind.SinglePrecision => "BitConverter.SingleToUInt32Bits(value)",
        ValueKind.DoublePrecision => "BitConverter.DoubleToUInt64Bits(value)",
        ValueKind.SignedByte or ValueKind.SignedShort or ValueKind.SignedWord or ValueKind.SignedLong
            or ValueKind.UnsignedByte or ValueKind.UnsignedShort or ValueKind.UnsignedWord or ValueKind.UnsignedLong
            or ValueKind.Enum => "unchecked((ulong)value)",
        ValueKind.Void or ValueKind.Text or ValueKind.Data or ValueKind.Struct or ValueKind.List
            or ValueKind.Interface or ValueKind.AnyPointer => throw new InvalidDataException("Expected scalar type."),
        _ => throw new InvalidDataException("Unknown scalar type.")
    };

    private string PointerType(SchemaType type, string list) => type.Kind switch
    {
        ValueKind.Text or ValueKind.Data => "ReadOnlySpan<byte>",
        ValueKind.Struct => analysis.Qualified(type.Target) + ".Reader",
        ValueKind.List => list + "ListReader",
        ValueKind.AnyPointer => "PointerReader",
        ValueKind.Void or ValueKind.Bool or ValueKind.SignedByte or ValueKind.SignedShort or ValueKind.SignedWord or ValueKind.SignedLong
            or ValueKind.UnsignedByte or ValueKind.UnsignedShort or ValueKind.UnsignedWord or ValueKind.UnsignedLong or ValueKind.SinglePrecision
            or ValueKind.DoublePrecision or ValueKind.Enum or ValueKind.Interface => throw new InvalidDataException("Expected pointer type."),
        _ => throw new InvalidDataException("Unknown pointer type.")
    };

    private string ReadPointer(SchemaType type, string slot, string list, string fallback)
    {
        string arguments = fallback.Length == 0 ? "" : ", " + fallback;
        return type.Kind switch
        {
            ValueKind.Text => $"{slot}.AsText({fallback})",
            ValueKind.Data => $"{slot}.AsData({fallback})",
            ValueKind.Struct => $"new {analysis.Qualified(type.Target)}.Reader({slot}.AsStruct({fallback}))",
            ValueKind.List => $"new {list}ListReader({slot}.AsList({ListSize(type.Element!)}{arguments}))",
            ValueKind.AnyPointer => fallback.Length == 0 ? slot : $"({slot}.IsNull ? {fallback} : {slot})",
            ValueKind.Void or ValueKind.Bool or ValueKind.SignedByte or ValueKind.SignedShort or ValueKind.SignedWord or ValueKind.SignedLong
                or ValueKind.UnsignedByte or ValueKind.UnsignedShort or ValueKind.UnsignedWord or ValueKind.UnsignedLong or ValueKind.SinglePrecision
                or ValueKind.DoublePrecision or ValueKind.Enum or ValueKind.Interface => throw new InvalidDataException("Expected pointer type."),
            _ => throw new InvalidDataException("Unknown pointer type.")
        };
    }

    private void PointerWrite(SchemaType type, string name, string slot, string select, HashSet<string> members, string scope)
    {
        string method = (type.Kind is ValueKind.Struct or ValueKind.List ? "Init" : "Set") + name;
        Analysis.Claim(members, method, scope);
        string signature = type.Kind switch
        {
            ValueKind.Text => $"void {method}(string? value) {{ {select}{slot}.SetText(value); }}",
            ValueKind.Data => $"void {method}(scoped ReadOnlySpan<byte> value) {{ {select}{slot}.SetData(value); }}",
            ValueKind.AnyPointer => $"void {method}(PointerReader value) {{ {select}{slot}.SetCopy(value); }}",
            ValueKind.Struct => $"{analysis.Qualified(type.Target)}.Builder {method}() {{ {select}return new({slot}.InitStruct({analysis.Qualified(type.Target)}.Layout)); }}",
            ValueKind.List => $"{name}ListBuilder {method}(int count) {{ {select}return new({InitList(type.Element!, slot)}); }}",
            ValueKind.Void or ValueKind.Bool or ValueKind.SignedByte or ValueKind.SignedShort or ValueKind.SignedWord or ValueKind.SignedLong
                or ValueKind.UnsignedByte or ValueKind.UnsignedShort or ValueKind.UnsignedWord or ValueKind.UnsignedLong or ValueKind.SinglePrecision
                or ValueKind.DoublePrecision or ValueKind.Enum or ValueKind.Interface => throw new InvalidDataException("Expected pointer type."),
            _ => throw new InvalidDataException("Unknown pointer type.")
        };
        Line("public " + signature);
    }

    private void ListTypes(SchemaType element, string name, HashSet<string> members, string scope)
    {
        string reader = name + "ListReader";
        string builder = name + "ListBuilder";
        Analysis.Claim(members, reader, scope);
        Analysis.Claim(members, builder, scope);
        string nested = name + "Element";
        if (element.Kind == ValueKind.List)
        {
            ListTypes(element.Element!, nested, members, scope);
        }

        int width = Analysis.Width(element.Kind);
        Open($"public readonly ref struct {reader}");
        Line("private readonly ListReader wire;");
        Line($"public {reader}(ListReader wire) => this.wire = wire;");
        Line("public int Count => wire.Count;");
        if (width != 0)
        {
            string itemType = width > 0 ? TypeName(element) : PointerType(element, nested);
            string value = width > 0 ? FromBits(element, $"wire.ReadScalar(index, {width})")
                : element.Kind == ValueKind.Struct
                    ? $"new {analysis.Qualified(element.Target)}.Reader(wire.GetStruct(index))"
                    : ReadPointer(element, "wire.GetPointer(index)", nested, "");
            Line($"public {itemType} this[int index] => {value};");
            if (element.Kind == ValueKind.Text)
            {
                Line("public string? Decode(int index) => wire.GetPointer(index).DecodeText();");
            }

            if (width < 0 && element.Kind != ValueKind.Struct)
            {
                Line("public bool Has(int index) => !wire.GetPointer(index).IsNull;");
            }

            Line("public Enumerator GetEnumerator() => new(this);");
            Open("public ref struct Enumerator");
            Line($"private readonly {reader} list;");
            Line("private int index;");
            Line($"internal Enumerator({reader} list) {{ this.list = list; index = -1; }}");
            Line("public bool MoveNext() => ++index < list.Count;");
            Line($"public {itemType} Current => list[index];");
            Close();
        }

        Close();
        Open($"public readonly ref struct {builder}");
        Line("private readonly ListBuilder wire;");
        Line($"public {builder}(ListBuilder wire) => this.wire = wire;");
        Line("public int Count => wire.Count;");
        if (width > 0)
        {
            Line($"public void Set(int index, {TypeName(element)} value) => wire.SetScalar(index, {ToBits(element)});");
        }
        else if (element.Kind == ValueKind.Struct)
        {
            Line($"public {analysis.Qualified(element.Target)}.Builder this[int index] => new(wire.GetStruct(index));");
        }
        else if (width < 0)
        {
            switch (element.Kind)
            {
                case ValueKind.Text: Line("public void Set(int index, string? value) => wire.GetPointer(index).SetText(value);"); break;
                case ValueKind.Data: Line("public void Set(int index, scoped ReadOnlySpan<byte> value) => wire.GetPointer(index).SetData(value);"); break;
                case ValueKind.AnyPointer: Line("public void Set(int index, PointerReader value) => wire.GetPointer(index).SetCopy(value);"); break;
                case ValueKind.List:
                    Line($"public {nested}ListBuilder Init(int index, int count) => new({InitList(element.Element!, "wire.GetPointer(index)")});");
                    break;
                case ValueKind.Void or ValueKind.Bool or ValueKind.SignedByte or ValueKind.SignedShort or ValueKind.SignedWord
                    or ValueKind.SignedLong or ValueKind.UnsignedByte or ValueKind.UnsignedShort or ValueKind.UnsignedWord
                    or ValueKind.UnsignedLong or ValueKind.SinglePrecision or ValueKind.DoublePrecision or ValueKind.Enum
                    or ValueKind.Struct or ValueKind.Interface:
                    throw new InvalidDataException("Unsupported list element.");
                default:
                    throw new InvalidDataException("Unknown list element.");
            }

            Line("public void Clear(int index) => wire.GetPointer(index).Clear();");
        }

        Close();
    }

    private string InitList(SchemaType type, string slot) => type.Kind == ValueKind.Struct
        ? $"{slot}.InitStructList(count, {analysis.Qualified(type.Target)}.Layout)"
        : $"{slot}.InitList({ListSize(type)}, count)";

    private static string ListSize(SchemaType type)
    {
        string kind = type.Kind == ValueKind.Struct ? "Composite" : Analysis.Width(type.Kind) switch
        {
            0 => "Void",
            1 => "Bit",
            8 => "Byte",
            16 => "TwoBytes",
            32 => "FourBytes",
            64 => "EightBytes",
            _ => "PointerWord"
        };
        return "ListElementSize." + kind;
    }
}
