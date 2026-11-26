namespace CMakeWorkflow.IntegrationTests.Support;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    internal string Output => StandardOutput + StandardError;
}
