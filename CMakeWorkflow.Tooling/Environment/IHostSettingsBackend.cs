namespace CMakeWorkflow.Tooling.Environment;

public interface IHostSettingsBackend
{
    string? Read(string path);
    void Write(string path, string value);
}
