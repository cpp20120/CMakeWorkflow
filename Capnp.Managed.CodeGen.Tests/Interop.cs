using System.Diagnostics;
using System.Collections.Immutable;
using System.Text;
using New = Capnp.Generated.Evolution_v2;
using Old = Capnp.Generated.Evolution_v1;
using Capnp.Generated.Probe;
using Capnp.Managed.CodeGen.Fixtures;
using Capnp.Managed.Schema;

namespace Capnp.Managed.CodeGen.Tests;

internal static partial class Interop
{
    internal static void Run(string compiler, string oracle, byte[] canonical)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("capnp-codegen-oracle-");
        try
        {
            string schema = Path.Combine(AppContext.BaseDirectory, "Schema");
            string[] files = [.. Directory.GetFiles(schema, "*.capnp").Order(StringComparer.Ordinal)];
            byte[] request = ReadProcess(compiler, ["compile", "-o-", "--src-prefix=" + schema, .. files]);
            using MemoryStream input = new(request);
            SchemaRequest model = Bootstrap.Read(input);
            ImmutableArray<GeneratedFile> generated = CSharpGenerator.Generate(model);
            ImmutableArray<GeneratedFile> reordered = CSharpGenerator.Generate(model with { Files = [.. model.Files.Reverse()] });
            Require(generated.SequenceEqual(reordered), "Generation depends on request order.");
            foreach (GeneratedFile output in generated)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Generated", output.Name);
                Require(output.Text == File.ReadAllText(path), "Checked-in binding differs: " + output.Name);
            }

            CompareProtocol(request, model);
            TestDiagnostics(compiler, directory.FullName, model);
            string framed = Path.Combine(directory.FullName, "message.frame");
            string raw = Path.Combine(directory.FullName, "canonical.raw");
            foreach (string form in new[] { "single", "far", "double" })
            {
                _ = ReadProcess(oracle, ["generate", form, framed]);
                MessageReader message = FrameCodec.Read(File.ReadAllBytes(framed));
                bool shape = form switch
                {
                    "single" => message.SegmentCount == 1,
                    "far" => message.Root.Kind == PointerKind.Far,
                    // Blob's slot is checked only here to prove the actual landing-pad fixture shape.
                    "double" => message.SegmentCount > 1 && message.Root.AsStruct().GetPointer(1).Kind == PointerKind.DoubleFar,
                    _ => false
                };
                Require(shape && Fixture.Matches(message), "C++ fixture does not match generated reader: " + form);
                _ = ReadProcess(oracle, ["canonicalize", framed, raw]);
                Require(canonical.AsSpan().SequenceEqual(File.ReadAllBytes(raw))
                    && canonical.AsSpan().SequenceEqual(CanonicalMessage.CreateSegment(message)), "Canonical mismatch: " + form);
                Console.WriteLine($"Generated C# / C++ {form} fixture passed with {message.SegmentCount} segment(s).");
            }

            File.WriteAllBytes(framed, FrameCodec.Write(new MessageReader(canonical)));
            _ = ReadProcess(oracle, ["verify", framed, raw]);
            Require(canonical.AsSpan().SequenceEqual(File.ReadAllBytes(raw)), "C++ rejected or changed managed canonical bytes.");
            MessageBuilder defaults = new();
            _ = Probe.InitRoot(defaults);
            byte[] defaultSegment = CanonicalMessage.CreateSegment(new MessageReader(defaults.CopySegment()));
            File.WriteAllBytes(framed, FrameCodec.Write(new MessageReader(defaultSegment)));
            _ = ReadProcess(oracle, ["defaults", framed, raw]);
            Require(defaultSegment.AsSpan().SequenceEqual(File.ReadAllBytes(raw)), "C++ schema defaults differ.");
            CheckEvolution(compiler, oracle, schema, framed, raw);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    static partial void CompareProtocol(byte[] request, SchemaRequest model);

    private static void CheckEvolution(string compiler, string oracle, string schema, string framed, string raw)
    {
        string oldSchema = Path.Combine(schema, "evolution-v1.capnp");
        string newSchema = Path.Combine(schema, "evolution-v2.capnp");
        byte[] oldFrame = ReadProcess(compiler, ["encode", oldSchema, "History"],
            "(values = [0, 0, 77], busy = \"running\")"u8.ToArray());
        New.History.Reader newReader = New.History.ReadRoot(FrameCodec.Read(oldFrame));
        Require(newReader.Values[2].Value == 77 && newReader.Counter == 31, "Generated reader rejected official old layout.");

        MessageBuilder oldBuilder = new();
        Old.History.Builder old = Old.History.InitRoot(oldBuilder);
        old.InitValues(3).Set(2, 77);
        old.SetBusy("running");
        CompareReference(oracle, framed, raw, oldFrame, oldBuilder.CopySegment());

        byte[] newFrame = ReadProcess(compiler, ["encode", newSchema, "History"],
            "(values = [(value = 77, note = \"unknown\")], state = extra, newKind = 81, label = \"present\")"u8.ToArray());
        Old.History.Reader oldReader = Old.History.ReadRoot(FrameCodec.Read(newFrame));
        Require(oldReader.Values[0] == 77 && (ushort)oldReader.Which == 2 && (ushort)oldReader.State == 1,
            "Generated reader rejected official new layout.");
        MessageBuilder newBuilder = new();
        New.History.Builder next = New.History.InitRoot(newBuilder);
        New.History_Wrapped.Builder item = next.InitValues(1)[0];
        item.SetValue(77);
        item.SetNote("unknown");
        next.SetState(New.State.Extra);
        next.SetNewKind(81);
        next.SetLabel("present");
        byte[] staged = newBuilder.CopySegment();
        CompareReference(oracle, framed, raw, newFrame, staged);
        string fromNew = Encoding.UTF8.GetString(ReadProcess(compiler, ["decode", oldSchema, "History"],
            FrameCodec.Write(new MessageReader(staged))));
        Require(fromNew.Contains("values = [77]", StringComparison.Ordinal), "Official old schema rejected managed new data.");
        Console.WriteLine("Official C++ schema evolution and canonical identities passed.");
    }

    private static void CompareReference(string oracle, string framed, string raw, byte[] official, byte[] staged)
    {
        File.WriteAllBytes(framed, official);
        _ = ReadProcess(oracle, ["canonicalize-any", framed, raw]);
        Require(CanonicalMessage.CreateSegment(new MessageReader(staged)).AsSpan().SequenceEqual(File.ReadAllBytes(raw)),
            "Official evolution fixture canonical bytes differ.");
    }

    private static void TestDiagnostics(string compiler, string directory, SchemaRequest model)
    {
        SchemaNode node = model.Nodes.Values.First(n => n.Kind == SchemaKind.Struct && n.Fields.Length > 1 && !n.IsGroup);
        SchemaField field = node.Fields.First(f => f.Group == 0 && f.Type.Kind != ValueKind.Void);
        SchemaNode broken = node with { Fields = node.Fields.Replace(field, field with { Offset = uint.MaxValue }) };
        Reject(() => CSharpGenerator.Generate(model with { Nodes = model.Nodes.SetItem(node.Id, broken) }), "outside");

        SchemaNode probe = model.Nodes.Values.Single(n => n.DisplayName.EndsWith("probe.capnp:Probe", StringComparison.Ordinal));
        SchemaField wide = probe.Fields.Single(f => f.Name == "i8");
        SchemaNode overlap = probe with { Fields = probe.Fields.Replace(wide, wide with { Offset = 0 }) };
        Reject(() => CSharpGenerator.Generate(model with { Nodes = model.Nodes.SetItem(probe.Id, overlap) }), "Overlapping");

        string schema = Path.Combine(directory, "unsupported.capnp");
        foreach ((string text, string diagnostic) in new[]
        {
            ("struct Box(T) { value @0 :T; }", "generic declarations"),
            ("struct Collision { text @0 :Text; hasText @1 :Bool; }", "naming collision")
        })
        {
            File.WriteAllText(schema, "@0xae4e295da0f61761;\n" + text);
            byte[] bytes = ReadProcess(compiler, ["compile", "-o-", "--src-prefix=" + directory, schema]);
            using MemoryStream stream = new(bytes);
            Reject(() => CSharpGenerator.Generate(Bootstrap.Read(stream)), diagnostic);
        }
    }

    private static byte[] ReadProcess(string executable, string[] arguments, byte[]? standardInput = null)
    {
        ProcessStartInfo start = new(executable) { RedirectStandardOutput = true, RedirectStandardInput = standardInput is not null };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start oracle.");
        if (standardInput is not null)
        {
            process.StandardInput.BaseStream.Write(standardInput);
            process.StandardInput.Close();
        }

        using MemoryStream output = new();
        process.StandardOutput.BaseStream.CopyTo(output);
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Official tool did not exit.");
        }

        byte[] bytes = output.ToArray();
        string name = Path.GetFileNameWithoutExtension(executable);
        Require(process.ExitCode == 0, $"Tool '{name}' exited with {process.ExitCode}. "
            + (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8.GetString(bytes) : ""));
        return bytes;
    }

    private static void Reject(Action action, string diagnostic)
    {
        try
        {
            action();
        }
        catch (InvalidDataException error) when (error.Message.Contains(diagnostic, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidDataException("Expected generator diagnostic: " + diagnostic);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
