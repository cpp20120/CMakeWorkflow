using System.Diagnostics.CodeAnalysis;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Protocol;
#pragma warning restore IDE0130

[SuppressMessage("Design", "CA1064", Justification = "This internal RPC refusal is translated to a closed error DTO and must not become a public exception contract.")]
[SuppressMessage("Design", "CA1032", Justification = "ApplicationRefusal carries ApplicationError.Code for JSON-RPC error 1001; standard exception constructors cannot supply that machine-readable refusal category.")]
internal sealed class ApplicationRefusal(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
