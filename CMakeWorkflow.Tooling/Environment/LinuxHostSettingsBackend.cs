#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed class LinuxHostSettingsBackend : IHostSettingsBackend
{
    public string? Read(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

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
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Linux host settings only.");
        }

        File.WriteAllText(path, value + "\n");
    }
}
