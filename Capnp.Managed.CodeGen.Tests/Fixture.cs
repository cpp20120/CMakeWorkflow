using Capnp.Generated.Common;
using Capnp.Generated.Probe;

namespace Capnp.Managed.CodeGen.Fixtures;

internal static class Fixture
{
    internal static byte[] Build(bool transitions)
    {
        MessageBuilder message = new();
        Probe.Builder root = Probe.InitRoot(message);
        root.SetBool(false);
        root.SetI8(-9);
        root.SetI16(-987);
        root.SetI32(-87654);
        root.SetI64(-9876543210);
        root.SetU8(220);
        root.SetU16(54321);
        root.SetU32(87654321);
        root.SetU64(9876543210);
        root.SetSingle(BitConverter.UInt32BitsToSingle(0x80000000));
        root.SetDouble(BitConverter.UInt64BitsToDouble(0x7ff8000000000042));
        root.SetStatus((Status)99);
        root.SetText("");
        root.SetBlob(new byte[168]);
        root.InitChild().SetAmount(23);
        root.InitSamples(2).Set(1, 456);
        root.InitFlags(11).Set(10, true);
        root.InitStates(1).Set(0, Status.Running);
        root.InitChildren(2)[1].SetNote("nested");
        root.InitStrings(2).Set(1, "");
        root.InitBlobs(1).Set(0, "data"u8);
        root.InitMatrix(2).Init(1, 2).Set(1, 40000);
        root.InitNamed(1)[0].SetTag(Probe_Nested_Tag.B);
        root.SetTail(889);
        Probe_Payload.Builder payload = root.InitPayload();
        if (transitions)
        {
            Probe_Payload_Details.Builder previous = payload.InitDetails();
            previous.SetMarker(uint.MaxValue);
            previous.InitInner().SetText(new string('x', 8192));
            previous.SetEnabled(true);
            payload.SetName("obsolete");
            payload.SetCount(ulong.MaxValue);
        }

        Probe_Payload_Details.Builder details = payload.InitDetails();
        details.SetMarker(77);
        details.InitInner().SetText("active");
        details.SetEnabled(true);
        return message.CopySegment();
    }

    internal static bool Matches(MessageReader message)
    {
        Probe.Reader root = Probe.ReadRoot(message);
        int flags = 0;
        foreach (bool flag in root.Flags)
        {
            flags += flag ? 1 : 0;
        }

        Probe_Payload.Reader payload = root.Payload;
        Probe_Payload_Details.Reader details = payload.Details;
        return !root.Bool && root.I8 == -9 && root.I16 == -987 && root.I32 == -87654 && root.I64 == -9876543210
            && root.U8 == 220 && root.U16 == 54321 && root.U32 == 87654321 && root.U64 == 9876543210
            && BitConverter.SingleToUInt32Bits(root.Single) == 0x80000000
            && BitConverter.DoubleToUInt64Bits(root.Double) == 0x7ff8000000000042 && (ushort)root.Status == 99
            && root.HasText && root.TextUtf8.IsEmpty && root.Blob.Length == 168 && root.Child.Amount == 23
            && root.Samples[1] == 456 && flags == 1 && root.Flags[10] && root.States[0] == Status.Running
            && root.Children[1].NoteUtf8.SequenceEqual("nested"u8) && !root.Strings.Has(0) && root.Strings.Has(1)
            && root.Blobs[0].SequenceEqual("data"u8) && root.Matrix[1][1] == 40000
            && root.Named[0].Tag == Probe_Nested_Tag.B && root.Tail == 889 && details.Marker == 77
            && details.Enabled && details.Inner.TextUtf8.SequenceEqual("active"u8);
    }

    internal static bool Defaults(MessageReader message)
    {
        Probe.Reader root = Probe.ReadRoot(message);
        return root.Bool && root.I8 == -7 && root.I16 == -500 && root.I32 == -70000 && root.I64 == -9000000000
            && root.U8 == 150 && root.U16 == 900 && root.U32 == 70000 && root.U64 == 9000000000
            && BitConverter.SingleToUInt32Bits(root.Single) == 0
            && BitConverter.SingleToUInt32Bits(root.SignedDefault) == 0x80000000 && double.IsPositiveInfinity(root.Double)
            && root.Status == Status.Running && root.TextUtf8.SequenceEqual("default"u8)
            && root.Blob is [1, 255] && root.Child.Amount == 19
            && root.Child.NoteUtf8.SequenceEqual("child"u8) && root.Samples[0] == 2 && root.Samples[1] == 3;
    }
}
