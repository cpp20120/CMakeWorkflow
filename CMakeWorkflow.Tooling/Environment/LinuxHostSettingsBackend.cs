using System.Globalization;

namespace CMakeWorkflow.Tooling;

public sealed class LinuxHostSettingsBackend : IHostSettingsBackend
{
    public string? Read(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(string path, string value)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux host settings only.");
        File.WriteAllText(path, value + "\n");
    }
}
