using System.Diagnostics.CodeAnalysis;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public static class HostConnection
{
    public static JsonRpc Create(Stream stream)
    {
        var rpc = new ApplicationRpc(new HeaderDelimitedMessageHandler(stream, stream, CreateFormatter()));
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
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "All RPC DTOs use HostJson source-generated metadata.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "All RPC DTOs use HostJson source-generated metadata.")]
    private static SystemTextJsonFormatter CreateFormatter() => new()
    {
        JsonSerializerOptions = new(HostJson.Default.Options) { TypeInfoResolver = HostJson.Default }
    };
}
