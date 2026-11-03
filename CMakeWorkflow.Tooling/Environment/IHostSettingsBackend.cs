using System.Globalization;

namespace CMakeWorkflow.Tooling;

public interface IHostSettingsBackend
{
    string? Read(string path);
    void Write(string path, string value);
}
