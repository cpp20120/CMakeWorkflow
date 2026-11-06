// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Environment;
#pragma warning restore IDE0130

public interface IHostSettingsBackend
{
    string? Read(string path);
    void Write(string path, string value);
}
