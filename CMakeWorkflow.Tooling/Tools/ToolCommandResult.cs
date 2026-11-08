namespace CMakeWorkflow.Tooling.Tools;

public sealed record ToolCommandResult(int ExitCode, string Output, string Error);
