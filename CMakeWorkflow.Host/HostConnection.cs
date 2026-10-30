using System.Diagnostics.CodeAnalysis;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public static class HostConnection
{
    public static JsonRpc Create(Stream stream) =>
        new(new HeaderDelimitedMessageHandler(stream, stream, CreateFormatter()));

    // Only the closed DTO contract in HostJson is allowed. No marshal-by-reference
    // objects, dynamic proxies, IProgress<T> or IAsyncEnumerable<T> cross the wire.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "All RPC DTOs use HostJson source-generated metadata.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "All RPC DTOs use HostJson source-generated metadata.")]
    private static SystemTextJsonFormatter CreateFormatter() => new()
    {
        JsonSerializerOptions = new(HostJson.Default.Options) { TypeInfoResolver = HostJson.Default }
    };
}
