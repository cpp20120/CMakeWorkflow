using System.Diagnostics.CodeAnalysis;
using CMakeWorkflow.Host.Protocol;
using StreamJsonRpc;

namespace CMakeWorkflow.Host.Transport;

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

#pragma warning disable CA2000 // The handler transfers to ApplicationRpc after construction succeeds.
        HeaderDelimitedMessageHandler handler = new(output, input, CreateFormatter());
#pragma warning restore CA2000
        ApplicationRpc rpc;
        try
        {
#pragma warning disable CA2000 // ApplicationRpc owns its handler after construction; caller owns the returned RPC.
            rpc = new(handler);
#pragma warning restore CA2000
        }
        catch (Exception constructionFailure)
        {
            try
            {
                ((IDisposable)handler).Dispose();
            }
#pragma warning disable CA1031 // Preserve both a failed RPC constructor and a failed handler cleanup.
            catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
#pragma warning restore CA1031
            {
                throw new AggregateException("RPC construction and handler cleanup both failed.",
                    constructionFailure, cleanupFailure);
            }

            throw;
        }

#if RPC_CANCELLATION_TESTING
        try
        {
            rpc.TraceSource.Switch.Level = System.Diagnostics.SourceLevels.All;
            _ = rpc.TraceSource.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        }
        catch (Exception traceFailure)
        {
            try
            {
                rpc.Dispose();
            }
#pragma warning disable CA1031 // Preserve trace setup failure even if RPC cleanup also fails.
            catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
#pragma warning restore CA1031
            {
                throw new AggregateException("RPC trace setup and cleanup both failed.",
                    traceFailure, cleanupFailure);
            }

            throw;
        }
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
