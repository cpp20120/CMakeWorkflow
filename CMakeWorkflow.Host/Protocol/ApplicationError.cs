// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Protocol;
#pragma warning restore IDE0130

public sealed record ApplicationError(string Code, string Message);
