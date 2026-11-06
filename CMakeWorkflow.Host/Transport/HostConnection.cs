using System.Diagnostics.CodeAnalysis;
using CMakeWorkflow.Host.Protocol;
using StreamJsonRpc;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Transport;
#pragma warning restore IDE0130

/// <summary>Creates header-delimited JSON-RPC connections using closed source-generated Host DTOs.</summary>
/// <remarks>
/// No local targets are registered and listening has not started. The caller configures the session and owns disposal.
/// Application failures use code 1001 with ApplicationError data; no CLR exception graph crosses the wire.
/// </remarks>
public static class HostConnection
{
    /// <summary>Creates a connection using one duplex stream; the caller owns the returned RPC lifetime.</summary>
    public static JsonRpc Create(Stream stream) => Create(stream, stream);

    /// <summary>Creates a connection with separate binary input/output streams.</summary>
    /// <remarks>
    /// The returned RPC owns its message handler and its underlying streams. The caller must register a session,
    /// start listening, and dispose/drain connection-owned work; creating an RPC does not own durable operation lifetime.
    /// </remarks>
    public static JsonRpc Create(Stream input, Stream output)
    {

#pragma warning disable CA2000 // The returned JsonRpc owns its message handler; the caller owns and disposes the connection.
        ApplicationRpc rpc = new(new HeaderDelimitedMessageHandler(output, input, CreateFormatter()));
#pragma warning restore CA2000

#if RPC_CANCELLATION_TESTING
        rpc.TraceSource.Switch.Level = System.Diagnostics.SourceLevels.All;
        rpc.TraceSource.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
#endif
        return rpc;
    }

    // Application errors use a code outside JSON-RPC's reserved range and a
    // closed DTO, not CommonErrorData or serialized CLR exception objects.
    private sealed class ApplicationRpc(IJsonRpcMessageHandler handler) : JsonRpc(handler)
    {
        protected override Type? GetErrorDetailsDataType(StreamJsonRpc.Protocol.JsonRpcError error) =>
            (int?)error.Error?.Code == 1001 ? typeof(ApplicationError) : base.GetErrorDetailsDataType(error);
    }

    // Only the closed DTO contract in HostJson is allowed. No marshal-by-reference
    // objects, dynamic proxies, IProgress<T> or IAsyncEnumerable<T> cross the wire.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "All RPC DTOs use HostJson source-generated metadata.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "All RPC DTOs use HostJson source-generated metadata.")]
    private static SystemTextJsonFormatter CreateFormatter() => new()
    {
        JsonSerializerOptions = new(HostJson.Default.Options) { TypeInfoResolver = HostJson.Default }
    };
}
