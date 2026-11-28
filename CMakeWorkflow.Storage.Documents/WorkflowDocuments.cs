using System.Text.Json;
using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.CMake.Reporting;
using Binding = Capnp.Generated.ExecutionBinding;

namespace CMakeWorkflow.Storage.Documents;

internal sealed record EncodedDocument(DocumentDescriptor Descriptor, byte[] Bytes);

internal static class WorkflowDocuments
{
    internal const int MaxStoredBytes = (16 * 1024 * 1024) + 8;
    internal static readonly MessageLimits ViewPolicy = MessageLimits.Default with
    {
        MaxMessageBytes = MaxStoredBytes - 8,
        MaxSegmentBytes = MaxStoredBytes - 8,
        MaxSegments = 1,
        MaxListElements = 16_384,
        MaxTextBytes = 16_384,
        MaxNesting = 32,
        MaxTraversalWords = 4 * 1024 * 1024
    };

    internal static EncodedDocument Encode(WorkflowDocument document, bool production)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!production)
        {
            ValidateGraph(document);
        }

        DocumentCodec codec = production ? DocumentCodec.ProductionWorkflowCapnp : DocumentCodec.WorkflowCapnp;
        byte[] segment = CapnpWire.Canonicalize(production
            ? ProductionWorkflowCodec.Encode(document) : WorkflowEncoding.Encode(document));
        _ = ReadSegment(segment, codec);
        return Binary(segment, CapnpWire.Frame(segment), codec, document.SchemaVersion);
    }

    internal static EncodedDocument ImportBinary(ReadOnlySpan<byte> frame, bool production)
    {
        CheckSize(frame.Length);
        byte[] owned = frame.ToArray();
        ReadOnlySpan<byte> segment = CapnpWire.Unframe(owned);
        RequireCanonical(segment);
        DocumentCodec codec = production ? DocumentCodec.ProductionWorkflowCapnp : DocumentCodec.WorkflowCapnp;
        WorkflowDocument decoded = ReadSegment(segment, codec);
        return Binary(segment, owned, codec, decoded.SchemaVersion);
    }

    internal static WorkflowDocument ReadBinary(ReadOnlySpan<byte> bytes, DocumentCodec codec) =>
        ReadSegment(CapnpWire.Unframe(bytes), codec);

    internal static EncodedDocument ImportExecutionPlan(ReadOnlySpan<byte> frame)
    {
        CheckSize(frame.Length);
        byte[] owned = frame.ToArray();
        ReadOnlySpan<byte> segment = CapnpWire.Unframe(owned);
        RequireCanonical(segment);
        VerifyExecutionPlan(owned);
        return Binary(segment, owned, DocumentCodec.ExecutionPlanCapnp, 1);
    }

    internal static EncodedDocument EncodeExecutionReport(NativeExecutionReport report)
    {
        byte[] frame = NativeExecutionReportCodec.Encode(report);
        CheckSize(frame.Length);
        return Binary(CapnpWire.Unframe(frame), frame, DocumentCodec.ExecutionReportCapnp, 1);
    }

    private static void VerifyExecutionPlan(ReadOnlySpan<byte> frame)
    {
        Binding.DispatchBinding.Reader reader = Binding.DispatchBinding.ReadRoot(FrameCodec.Read(frame, ViewPolicy));
        string? operation = reader.DecodeOperationId();
        string? request = reader.DecodeRequestDigest();
        if (reader.Version != 1 || operation is not { Length: 32 } || !operation.All(Uri.IsHexDigit)
            || request is not { Length: 64 } || !request.All(Uri.IsHexDigit)
            || !ArtifactStoreFiles.IsCanonicalHex(reader.DecodeDocumentId(), 64)
            || !ArtifactStoreFiles.IsCanonicalHex(reader.DecodeBlobId(), 64)
            || !ArtifactStoreFiles.IsCanonicalHex(reader.DecodeCanonicalDigest(), 64)
            || string.IsNullOrEmpty(reader.DecodeWorkspaceId()) || string.IsNullOrEmpty(reader.DecodeManifestPath())
            || string.IsNullOrEmpty(reader.DecodeResultsDirectory()) || reader.Jobs == 0
            || reader.Nodes.Count == 0)
        {
            throw new InvalidDataException("Invalid execution plan binding.");
        }
    }

    private static WorkflowDocument ReadSegment(ReadOnlySpan<byte> segment, DocumentCodec codec)
    {
        if (codec == DocumentCodec.ProductionWorkflowCapnp)
        {
            return ProductionWorkflowCodec.Decode(segment);
        }

        WorkflowDocument workflow = WorkflowEncoding.Decode(segment);
        ValidateGraph(workflow);
        return workflow;
    }

    internal static EncodedDocument ImportJson(ReadOnlySpan<byte> json, LegacyDigest expected)
    {
        CheckSize(json.Length);
        return ImportOwnedJson(json.ToArray(), expected).Encoded;
    }

    internal static (EncodedDocument Encoded, WorkflowDocument Document) ImportOwnedJson(byte[] bytes, LegacyDigest expected)
    {
        CheckSize(bytes.Length);
        BlobId blob = new(DocumentDescriptor.Hash(bytes));
        if (blob.Value != expected.Value)
        {
            throw new InvalidDataException("Legacy SHA-256 does not match the exact JSON bytes.");
        }

        WorkflowDocument doc = ReadJson(bytes);
        return (new(DocumentDescriptor.Create(DocumentCodec.WorkflowJson, doc.SchemaVersion, blob, null, bytes.Length), bytes), doc);
    }

    internal static VerificationReport Verify(ReadOnlySpan<byte> bytes, DocumentDescriptor expected, VerificationMode mode)
    {
        return bytes.Length != expected.StoredLength || DocumentDescriptor.Hash(bytes) != expected.Blob.Value
            ? throw new InvalidDataException("Stored workflow bytes fail length or SHA-256 verification.")
            : mode switch
            {
                VerificationMode.StoredBytes => new(true, false, false, false, false),
                VerificationMode.Full => VerifyContent(bytes, expected),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
    }

    private static VerificationReport VerifyContent(ReadOnlySpan<byte> bytes, DocumentDescriptor expected)
    {
        if (expected.Codec == DocumentCodec.WorkflowJson)
        {
            return ReadJson(bytes).SchemaVersion != expected.SchemaVersion
                ? throw new InvalidDataException("Legacy schema version differs from its descriptor.")
                : new(true, false, false, true, false);
        }

        ReadOnlySpan<byte> segment = CapnpWire.Unframe(bytes);
        if (CapnpWire.HashCanonical(segment) != expected.Canonical?.Value)
        {
            throw new InvalidDataException("Canonical workflow digest differs from its descriptor.");
        }

        RequireCanonical(segment);
        if (expected.Codec == DocumentCodec.ExecutionPlanCapnp)
        {
            VerifyExecutionPlan(bytes);
            return new(true, true, true, true, false);
        }

        if (expected.Codec == DocumentCodec.ExecutionReportCapnp)
        {
            _ = NativeExecutionReportCodec.Decode(bytes);
            return new(true, true, true, true, false);
        }

        WorkflowDocument workflow = ReadSegment(segment, expected.Codec);
        return workflow.SchemaVersion != expected.SchemaVersion
            ? throw new InvalidDataException("Workflow version differs from its descriptor.")
            : new(true, true, true, true, expected.Codec == DocumentCodec.WorkflowCapnp);
    }

    internal static WorkflowDocument ReadJson(ReadOnlySpan<byte> bytes)
    {
        RejectDuplicateProperties(bytes);
        WorkflowDocument? document = JsonSerializer.Deserialize(bytes, WorkflowDefinitionJson.Default.WorkflowDocument);
        return document is null || document.SchemaVersion is not (1 or 2) || document.Nodes is null || document.Contexts is null
            ? throw new InvalidDataException("Unsupported legacy workflow schema or invalid document.")
            : document;
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        // The production DTO reader accepts duplicate object members. A conversion must not silently discard one.
        // Scan names once over bounded input, then reuse production source-generated serialization.
        Utf8JsonReader reader = new(bytes);
        Stack<HashSet<string>> objects = new();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objects.Push(new(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                _ = objects.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!))
            {
                throw new InvalidDataException("Duplicate legacy JSON object member.");
            }
        }
    }

    internal static void NormalizeProjection(WorkflowDocument workflow, bool preserveDependencyOrder)
    {
        workflow.Contexts = workflow.Contexts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        Dictionary<string, int> declaration = new(StringComparer.Ordinal);
        if (!preserveDependencyOrder)
        {
            for (int i = 0; i < workflow.Nodes.Count; i++)
            {
                declaration.Add(workflow.Nodes[i].Id, i);
            }
        }

        foreach (WorkflowNode node in workflow.Nodes)
        {
            if (preserveDependencyOrder)
            {
                node.Options = node.Options.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }
            else
            {
                node.DependsOn = [.. node.DependsOn.Distinct(StringComparer.Ordinal).OrderBy(id => declaration[id])];
            }

            node.Environment = node.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
    }

    internal static byte[] Project(WorkflowDocument workflow) =>
        JsonSerializer.SerializeToUtf8Bytes(workflow, WorkflowDefinitionJson.Default.WorkflowDocument);

    // This is offline validation for the experimental codec subset. The production planner retains host/admission checks.
    internal static void ValidateGraph(WorkflowDocument workflow)
    {
        if (workflow.SchemaVersion != 2 || workflow.Contexts is null || workflow.Nodes is not { Count: > 0 and <= 16_384 })
        {
            throw new InvalidDataException("Binary persistence requires a nonempty schema-v2 workflow.");
        }

        Dictionary<string, WorkflowNode> nodes = new(workflow.Nodes.Count, StringComparer.Ordinal);
        foreach (WorkflowNode node in workflow.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || !nodes.TryAdd(node.Id, node)
                || node.DependsOn is null || node.Inputs is null || node.Outputs is null || node.Arguments is null
                || node.Environment is null || node.Resources is null || node.Options is null
                || (node.Kind != "run" && (node.Context is null || !workflow.Contexts.ContainsKey(node.Context)))
                || (node.TimeoutSeconds is double timeout && (!double.IsFinite(timeout) || timeout <= 0)))
            {
                throw new InvalidDataException("Invalid workflow node or context reference.");
            }
        }

        Dictionary<string, int> remaining = new(nodes.Count, StringComparer.Ordinal);
        Dictionary<string, List<string>> successors = new(StringComparer.Ordinal);
        Queue<string> ready = new();
        foreach (WorkflowNode node in workflow.Nodes)
        {
            HashSet<string> dependencies = new(node.DependsOn, StringComparer.Ordinal);
            if (node.Kind == "run")
            {
                if (node.From is null || !nodes.TryGetValue(node.From, out WorkflowNode? source) || source.Kind != "build")
                {
                    throw new InvalidDataException("Run source must identify a build node.");
                }

                _ = dependencies.Add(node.From);
            }

            foreach (WorkflowArtifactInput input in node.Inputs)
            {
                if (input is null || (input.From is null ? input.Path is null || input.Artifact is not null
                    : !nodes.TryGetValue(input.From, out WorkflowNode? producer) || string.IsNullOrEmpty(input.Artifact)
                        || !producer.Outputs.Exists(output => output is not null && output.Name == input.Artifact)))
                {
                    throw new InvalidDataException("Invalid workflow artifact reference.");
                }

                if (input.From is not null)
                {
                    _ = dependencies.Add(input.From);
                }
            }

            foreach (string dependency in dependencies)
            {
                if (dependency is null || !nodes.ContainsKey(dependency))
                {
                    throw new InvalidDataException("Unknown workflow dependency.");
                }

                if (!successors.TryGetValue(dependency, out List<string>? children))
                {
                    children = [];
                    successors.Add(dependency, children);
                }

                children.Add(node.Id);
            }

            remaining.Add(node.Id, dependencies.Count);
            if (dependencies.Count == 0)
            {
                ready.Enqueue(node.Id);
            }
        }

        int visited = 0;
        while (ready.TryDequeue(out string? node))
        {
            visited++;
            if (successors.TryGetValue(node, out List<string>? children))
            {
                foreach (string child in children)
                {
                    if (--remaining[child] == 0)
                    {
                        ready.Enqueue(child);
                    }
                }
            }
        }

        if (visited != nodes.Count)
        {
            throw new InvalidDataException("Workflow dependency cycle.");
        }
    }

    private static EncodedDocument Binary(ReadOnlySpan<byte> segment, byte[] frame, DocumentCodec codec, int schemaVersion) =>
        new(DocumentDescriptor.Create(codec, schemaVersion, new(DocumentDescriptor.Hash(frame)),
            new(CapnpWire.HashCanonical(segment)), frame.Length), frame);

    private static void RequireCanonical(ReadOnlySpan<byte> segment)
    {
        if (!segment.SequenceEqual(CapnpWire.Canonicalize(segment)))
        {
            throw new InvalidDataException("Workflow segment is not in official canonical form.");
        }
    }

    internal static void CheckSize(int size)
    {
        if (size is < 1 or > MaxStoredBytes)
        {
            throw new InvalidDataException("Workflow document exceeds its byte limit or is empty.");
        }
    }
}
