namespace CMakeWorkflow.Host;

internal sealed class ApplicationRefusal(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
