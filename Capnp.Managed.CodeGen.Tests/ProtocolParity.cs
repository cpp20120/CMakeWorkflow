#if CAPNP_PROTOCOL_PROOF
using Capnp.Generated.Schema;
using Capnp.Managed.Schema;

namespace Capnp.Managed.CodeGen.Tests;

internal static partial class Interop
{
    static partial void CompareProtocol(byte[] request, SchemaRequest model)
    {
        CodeGeneratorRequest.Reader generated = CodeGeneratorRequest.ReadRoot(FrameCodec.Read(request));
        Require(generated.Nodes.Count == model.Nodes.Count && generated.RequestedFiles.Count == model.Files.Length,
            "Generated compiler protocol differs from bootstrap counts.");
        foreach (Node.Reader node in generated.Nodes)
        {
            SchemaNode expected = model.Nodes[node.Id];
            Require(node.DecodeDisplayName() == expected.DisplayName && (ushort)node.Which == (ushort)expected.Kind,
                "Generated protocol node differs from bootstrap.");
            if (expected.Kind != SchemaKind.Struct)
            {
                continue;
            }

            Node_Struct.Reader structure = node.Struct;
            Require(structure.DataWordCount == expected.DataWords && structure.PointerCount == expected.PointerWords
                && structure.DiscriminantOffset == expected.DiscriminantOffset && structure.Fields.Count == expected.Fields.Length,
                "Generated protocol struct layout differs from bootstrap.");
            for (int i = 0; i < structure.Fields.Count; i++)
            {
                Field.Reader field = structure.Fields[i];
                SchemaField comparison = expected.Fields[i];
                Require(field.DecodeName() == comparison.Name && field.DiscriminantValue == comparison.Discriminant
                    && (field.Which == Field.Variant.Group ? field.Group.TypeId == comparison.Group
                        : field.Slot.Offset == comparison.Offset && (ushort)field.Slot.Type.Which == (ushort)comparison.Type.Kind),
                    "Generated protocol field differs from bootstrap.");
            }
        }

        Console.WriteLine("Generated compiler protocol matched the checked-in bootstrap.");
    }
}
#endif
