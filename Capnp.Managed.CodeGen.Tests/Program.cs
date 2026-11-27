using System.Buffers.Binary;
using Capnp.Generated.Common;
using Capnp.Generated.Probe;
using Capnp.Managed;
using Capnp.Managed.CodeGen.Fixtures;
using Capnp.Managed.CodeGen.Tests;
using Capnp.Managed.Schema;
using Old = Capnp.Generated.Evolution_v1;
using New = Capnp.Generated.Evolution_v2;

byte[] fresh = CanonicalMessage.CreateSegment(new MessageReader(Fixture.Build(transitions: false)));
byte[] switched = CanonicalMessage.CreateSegment(new MessageReader(Fixture.Build(transitions: true)));
Check(fresh.AsSpan().SequenceEqual(switched) && Fixture.Matches(new MessageReader(switched)), "Union transition or fixture values.");
Check(CanonicalMessage.HashSegment(fresh) == "9729c70a8401a1d3a8938b65d8037c32f4c5e5c949138f9131cd413659ee8acf",
    "Official generated fixture golden hash differs.");
Probe.Reader zero = Probe.ReadRoot(new MessageReader(new byte[8]));
Check(Fixture.Defaults(new MessageReader(new byte[8])) && !zero.HasText && !zero.HasBlob && !zero.HasChild
    && !zero.HasSamples && Limit.Value == 1024 && Label.Value.SequenceEqual("constant"u8) && Numbers.Value[1] == 13,
    "Scalar, pointer defaults or constants.");
MessageBuilder builder = new();
Probe.Builder root = Probe.InitRoot(builder);
root.SetTextToDefault();
root.SetBlobToDefault();
root.SetChildToDefault();
root.SetSamplesToDefault();
Probe.Reader copied = Probe.ReadRoot(new MessageReader(builder.CopySegment()));
Check(copied.HasText && copied.HasBlob && copied.HasChild && copied.HasSamples
    && Fixture.Defaults(new MessageReader(builder.CopySegment())), "Defaults must be copied into the destination arena.");
root.SetText("");
root.SetBlob([]);
root.InitSamples(0);
Probe.Reader empty = Probe.ReadRoot(new MessageReader(builder.CopySegment()));
Check(empty.HasText && empty.TextUtf8.IsEmpty && empty.HasBlob && empty.Blob.IsEmpty
    && empty.HasSamples && empty.Samples.Count == 0, "Present empty values override defaults.");
try
{
    _ = empty.Payload.NameUtf8;
    throw new InvalidDataException("Inactive union getter succeeded.");
}
catch (InvalidOperationException)
{
}

MessageBuilder oldBuilder = new();
Old.History.Builder old = Old.History.InitRoot(oldBuilder);
old.InitValues(3).Set(2, 77);
old.SetBusy("running");
byte[] oldBytes = oldBuilder.CopySegment();
New.History.Reader newer = New.History.ReadRoot(new MessageReader(oldBytes));
Check(newer.Counter == 31 && newer.LabelUtf8.SequenceEqual("future"u8) && !newer.HasLabel
    && newer.Values[2].Value == 77 && newer.Values[2].NoteUtf8.IsEmpty
    && newer.BusyUtf8.SequenceEqual("running"u8), "New projection of old layout.");

MessageBuilder newBuilder = new();
New.History.Builder next = New.History.InitRoot(newBuilder);
New.History.ValuesListBuilder newValues = next.InitValues(3);
newValues[2].SetValue(77);
newValues[2].SetNote("unknown");
next.SetNewKind(123456);
next.SetState(New.State.Extra);
next.SetLabel("new");
byte[] newBytes = newBuilder.CopySegment();
Old.History.Reader older = Old.History.ReadRoot(new MessageReader(newBytes));
Check(older.Values[2] == 77 && (ushort)older.State == 1 && (ushort)older.Which == 2, "Old projection of new layout.");
New.History.Reader preserved = New.History.ReadRoot(new MessageReader(CanonicalMessage.CreateSegment(new MessageReader(newBytes))));
Check(preserved.NewKind == 123456 && preserved.Values[2].NoteUtf8.SequenceEqual("unknown"u8),
    "Unknown fields were lost during canonicalization.");

for (int i = 0; i < 250; i++)
{
    byte[] invalid = new byte[i];
    if (invalid.Length >= 8)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(invalid, (uint)(i * 13));
    }

    using MemoryStream corrupt = new(invalid);
    try
    {
        _ = Bootstrap.Read(corrupt);
        throw new InvalidOperationException("Malformed request accepted.");
    }
    catch (InvalidDataException)
    {
    }
}

if (args is ["--oracle", string compiler, string oracle])
{
    Interop.Run(compiler, oracle, fresh);
}
else if (args is ["--msbuild", string repository, string capnp, string plugin])
{
    Interop.BuildIntegration(repository, capnp, plugin);
}
else if (args.Length != 0)
{
    throw new ArgumentException("Use --oracle <capnp> <oracle> or --msbuild <repository> <capnp> <plugin>.");
}

Console.WriteLine($"Generated bindings, evolution, and malformed request tests passed. SHA-256 {CanonicalMessage.HashSegment(fresh)}");

static void Check(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidDataException(description);
    }
}
