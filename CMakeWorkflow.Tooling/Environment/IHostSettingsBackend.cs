#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public interface IHostSettingsBackend
{
    string? Read(string path);
    void Write(string path, string value);
}
