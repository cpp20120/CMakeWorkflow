using System.Text.Unicode;

namespace Capnp.Managed;

/// <summary>A borrowed pointer slot. Pointer defaults may come from a separate immutable schema message.</summary>
public readonly ref struct PointerReader
{
    private readonly MessageReader message;
    private readonly WordAddress address;
    private readonly ulong word;

    internal PointerReader(MessageReader message, WordAddress address)
    {
        this.message = message;
        this.address = address;
        word = message.SegmentCount == 0 ? 0 : message.ReadWord(address);
    }

    public bool IsNull => word == 0;
    public PointerKind Kind => Wire.Kind(word);

    public StructReader AsStruct() => new(message, message.ReadStruct(Resolve()));
    public StructReader AsStruct(PointerReader defaultValue) => (IsNull ? defaultValue : this).AsStruct();

    public ListReader AsList() => new(message, message.ReadList(Resolve()));

    public ListReader AsList(ListElementSize elementSize)
    {
        ListReader reader = AsList();
        reader.RequireCompatible(elementSize);
        return reader;
    }

    public ListReader AsList(ListElementSize elementSize, PointerReader defaultValue) =>
        (IsNull ? defaultValue : this).AsList(elementSize);

    public ReadOnlySpan<byte> AsData() => IsNull ? default : ReadBlob(text: false);
    public ReadOnlySpan<byte> AsData(PointerReader defaultValue) => (IsNull ? defaultValue : this).AsData();

    public ReadOnlySpan<byte> AsText() => IsNull ? default : ReadBlob(text: true);
    public ReadOnlySpan<byte> AsText(PointerReader defaultValue) => (IsNull ? defaultValue : this).AsText();

    public string? DecodeText() => IsNull ? null : Wire.Utf8.GetString(ReadBlob(text: true));
    public string? DecodeText(PointerReader defaultValue) => (IsNull ? defaultValue : this).DecodeText();

    internal MessageReader Message => message;
    internal WordAddress Address => address;

    private ResolvedPointer Resolve() => message.Resolve(address, word);

    private ReadOnlySpan<byte> ReadBlob(bool text)
    {
        ListLocation list = message.ReadList(Resolve());
        int limit = text ? message.Limits.MaxTextBytes : message.Limits.MaxDataBytes;
        if (list.Kind != ListElementSize.Byte || list.Count > limit || (text && list.Count == 0))
        {
            throw Wire.Invalid("Invalid blob element size or length.");
        }

        ReadOnlySpan<byte> bytes = message.Bytes(list.Start, list.Count);
        return text ? CheckText(bytes) : bytes;
    }

    private static ReadOnlySpan<byte> CheckText(ReadOnlySpan<byte> bytes) =>
        bytes[^1] == 0 && Utf8.IsValid(bytes[..^1])
            ? bytes[..^1]
            : throw Wire.Invalid("Text must be NUL-terminated UTF-8.");
}
