using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Storage.Documents;

public delegate TResult DocumentInspection<TResult>(ReadOnlySpan<byte> bytes);

/// <summary>Owns a shared physical blob lease and an immutable view until all inspections finish.</summary>
public sealed class DocumentSession : IDisposable
{
    private readonly Lock gate = new();
    private readonly FileStream lease;
    private readonly MappedMessage? mapping;
    private readonly byte[]? buffered;
    private int active;
    private bool disposed;

    internal DocumentSession(DocumentDescriptor descriptor, FileStream lease, byte[]? buffered,
        MappedMessage? mapping, VerificationMode verification)
    {
        Descriptor = descriptor;
        this.lease = lease;
        this.buffered = buffered;
        this.mapping = mapping;
        Verification = InspectStoredBytes(bytes => WorkflowDocuments.Verify(bytes, descriptor, verification));
    }

    public DocumentDescriptor Descriptor { get; }
    public VerificationReport Verification { get; }

    public TResult InspectStoredBytes<TResult>(DocumentInspection<TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return InspectBytes(inspect, static (bytes, callback) => callback(bytes));
    }

    private TResult InspectBytes<TState, TResult>(TState state, MappedMessage.ByteInspection<TState, TResult> inspect)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            active++;
        }

        try
        {
            return mapping is null ? inspect(buffered, state) : mapping.InspectBytes(state, inspect);
        }
        finally
        {
            lock (gate)
            {
                active--;
                if (active == 0 && disposed)
                {
                    Release();
                }
            }
        }
    }

    /// <summary>Passes a generated workflow view into a synchronous callback.</summary>
    public TResult InspectWorkflow<TResult>(WorkflowInspection<TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return Descriptor.Codec == DocumentCodec.WorkflowCapnp
            ? InspectWire(inspect, static (message, callback) => callback(new WorkflowView(message)))
            : throw new InvalidOperationException("The prototype workflow view requires its own codec.");
    }

    public TResult InspectWire<TResult>(MessageInspection<TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        return InspectWire(inspect, static (message, callback) => callback(message));
    }

    private TResult InspectWire<TState, TResult>(TState state, MessageInspection<TState, TResult> inspect) =>
        Descriptor.Codec == DocumentCodec.WorkflowJson
            ? throw new InvalidOperationException("A binary view requires a Cap’n Proto document.")
            : InspectBytes((state, inspect), static (bytes, capture) =>
                capture.inspect(FrameCodec.Read(bytes, WorkflowDocuments.ViewPolicy), capture.state));

    public WorkflowDocument Materialize() => Descriptor.Codec switch
    {
        DocumentCodec.WorkflowJson => InspectStoredBytes(WorkflowDocuments.ReadJson),
        DocumentCodec.WorkflowCapnp when Verification.Wire => InspectWorkflow(WorkflowEncoding.Materialize),
        DocumentCodec.ProductionWorkflowCapnp when Verification.Wire => InspectWire(ProductionWorkflowCodec.Materialize),
        DocumentCodec.WorkflowCapnp or DocumentCodec.ProductionWorkflowCapnp =>
            InspectBytes(Descriptor.Codec, static (bytes, codec) => WorkflowDocuments.ReadBinary(bytes, codec)),
        DocumentCodec.None => throw new InvalidDataException("Missing workflow codec."),
        DocumentCodec.ExecutionPlanCapnp => throw new InvalidOperationException("An execution plan has no workflow projection."),
        _ => throw new InvalidDataException("Unsupported workflow codec.")
    };

    public byte[] ExportStoredBytes() => InspectStoredBytes(static bytes => bytes.ToArray());

    public byte[] ExportJsonProjection() => WorkflowDocuments.Project(Materialize());

    public void Dispose()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                if (active == 0)
                {
                    Release();
                }
            }
        }
    }

    private void Release()
    {
        try
        {
            mapping?.Dispose();
        }
        finally
        {
            lease.Dispose();
        }
    }
}
