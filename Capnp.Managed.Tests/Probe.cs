namespace Capnp.Managed.Tests;

// A hand-written adapter with the layout and defaults emitted by the official compiler.
internal readonly ref struct Probe
{
    private readonly StructReader root;

    internal Probe(StructReader root)
    {
        this.root = root;
    }

    internal uint Number => (uint)root.ReadScalar(0, 32, 17);
    internal ReadOnlySpan<byte> Label => root.GetPointer(0).AsText(new MessageReader(LabelDefault).Root);
    internal ulong LeafNumber => root.GetPointer(1).AsStruct().ReadScalar(0, 64);
    internal ListReader Flags => root.GetPointer(2).AsList(ListElementSize.Bit);
    internal ListReader Samples => root.GetPointer(3).AsList(ListElementSize.TwoBytes);
    internal ListReader Entries => root.GetPointer(4).AsList(ListElementSize.Composite);
    internal ReadOnlySpan<byte> Blob => root.GetPointer(5).AsData();

    private static byte[] LabelDefault { get; } = MakeLabelDefault();

    internal static MessageBuilder Build(bool reverse)
    {
        MessageBuilder message = new();
        StructBuilder root = message.InitRoot(new(1, 6));
        root.SetScalar(0, 32, 31, 17);
        if (reverse)
        {
            BlobAndLists(root);
            ScalarPointers(root);
        }
        else
        {
            ScalarPointers(root);
            BlobAndLists(root);
        }

        return message;
    }

    private static void ScalarPointers(StructBuilder root)
    {
        root.GetPointer(0).SetText("managed");
        root.GetPointer(1).InitStruct(new(1, 0)).SetScalar(0, 64, 0x1122334455667788);
    }

    private static void BlobAndLists(StructBuilder root)
    {
        ListBuilder flags = root.GetPointer(2).InitList(ListElementSize.Bit, 11);
        flags.SetScalar(0, 1);
        flags.SetScalar(10, 1);
        ListBuilder samples = root.GetPointer(3).InitList(ListElementSize.TwoBytes, 3);
        samples.SetScalar(0, 7);
        samples.SetScalar(2, 255);
        ListBuilder entries = root.GetPointer(4).InitStructList(2, new(1, 1));
        entries.GetStruct(1).SetScalar(0, 16, 513);
        entries.GetStruct(0).GetPointer(0).SetText("entry");
        Span<byte> bytes = stackalloc byte[64];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        root.GetPointer(5).SetData(bytes);
    }

    private static byte[] MakeLabelDefault()
    {
        MessageBuilder message = new();
        message.Root.SetText("schema-default");
        return message.CopySegment();
    }
}
