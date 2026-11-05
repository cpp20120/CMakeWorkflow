#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.IntegrationTests;
#pragma warning restore IDE0130

internal sealed record CommandResult(int ExitCode, string Output);
