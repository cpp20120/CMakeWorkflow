// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.IntegrationTests.Support;
#pragma warning restore IDE0130

internal sealed record CommandResult(int ExitCode, string Output);
