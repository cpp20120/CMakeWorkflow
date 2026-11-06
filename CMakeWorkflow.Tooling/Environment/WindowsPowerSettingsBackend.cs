using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling.Environment;

public sealed partial class WindowsPowerSettingsBackend : IHostSettingsBackend
{
    public string? Read(string path)
    {
        Validate(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        uint error = PowerGetActiveScheme(IntPtr.Zero, out nint allocated);
        if (error != 0)
        {
            throw new IOException($"PowerGetActiveScheme failed with Win32 code {error}.");
        }

        try
        {
            return allocated == IntPtr.Zero
                ? throw new IOException("PowerGetActiveScheme returned no GUID.")
                : Marshal.PtrToStructure<Guid>(allocated).ToString("D");
        }
        finally
        {
            if (allocated != IntPtr.Zero)
            {
                _ = LocalFree(allocated);
            }
        }
    }

    public void Write(string path, string value)
    {
        Validate(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (!Guid.TryParseExact(value, "D", out Guid scheme))
        {
            throw new InvalidDataException("Invalid Windows power scheme GUID.");
        }

        uint error = PowerSetActiveScheme(IntPtr.Zero, in scheme);
        if (error != 0)
        {
            throw new IOException($"PowerSetActiveScheme failed with Win32 code {error} (scheme may not exist).");
        }
    }

    private static void Validate(string path)
    {
        if (path != WindowsHostSettingsProvisioner.SchemePath)
        {
            throw new InvalidDataException("Unexpected Windows host setting.");
        }
    }

    [LibraryImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr activeSchemeGuid);

    [LibraryImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerSetActiveScheme(IntPtr rootKey, in Guid schemeGuid);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LocalFree(IntPtr memory);
}