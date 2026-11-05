using System.Text.Json;
using StreamJsonRpc;

namespace CMakeWorkflow.Client;

/// <summary>A stable Host application refusal with a machine-readable code.</summary>
public sealed class CMakeWorkflowRpcRefusalException(string code, string message, Exception inner)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

internal static class RpcRefusals
{
    internal static async Task<T> TranslateAsync<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (RemoteInvocationException error) when (TryRead(error, out _, out _))
        {
            TryRead(error, out var code, out var message);
            throw new CMakeWorkflowRpcRefusalException(code!, message!, error);
        }
    }

    private static bool TryRead(RemoteInvocationException error, out string? code, out string? message)
    {
        code = null;
        message = null;
        if (error.ErrorCode != 1001) return false;
        switch (error.DeserializedErrorData)
        {
            case RpcErrorData data:
                code = data.Code;
                message = data.Message;
                break;
            case JsonElement element when element.ValueKind == JsonValueKind.Object:
                if (element.TryGetProperty("code", out var c) || element.TryGetProperty("Code", out c))
                    code = c.GetString();
                if (element.TryGetProperty("message", out var m) || element.TryGetProperty("Message", out m))
                    message = m.GetString();
                break;
        }
        return !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(message);
    }
}
