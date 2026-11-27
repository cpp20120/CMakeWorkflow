using Capnp.Managed;

namespace CMakeWorkflow.Binary.Capnp;

public delegate TResult WorkflowInspection<TResult>(WorkflowView workflow);

/// <summary>Provides a synchronous, borrowed view of an immutable framed workflow file.</summary>
public static class MappedWorkflow
{
    /// <remarks>The file must remain immutable for the entire callback. A concurrent truncation can fault on Unix.</remarks>
    public static TResult Read<TResult>(string path, WorkflowInspection<TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        using MappedMessage mapped = MappedMessage.Open(path, CapnpWire.ViewLimits);
        return mapped.Inspect(inspect, static (message, callback) => callback(new WorkflowView(message)));
    }
}
