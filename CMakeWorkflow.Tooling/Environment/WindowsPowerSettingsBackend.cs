using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling;

public sealed partial class WindowsPowerSettingsBackend : IHostSettingsBackend
{
    public string? Read(string path)
    {
        Validate(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var error = PowerGetActiveScheme(IntPtr.Zero, out var allocated);
        if (error != 0) throw new IOException($"PowerGetActiveScheme failed with Win32 code {error}.");
        try
        {
            if (allocated == IntPtr.Zero) throw new IOException("PowerGetActiveScheme returned no GUID.");
            return Marshal.PtrToStructure<Guid>(allocated).ToString("D");
        }
        finally
        {
            if (allocated != IntPtr.Zero) LocalFree(allocated);
        }
    }

    public void Write(string path, string value)
    {
        Validate(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Guid.TryParseExact(value, "D", out var scheme))
            throw new InvalidDataException("Invalid Windows power scheme GUID.");
        var error = PowerSetActiveScheme(IntPtr.Zero, in scheme);
        if (error != 0)
            throw new IOException($"PowerSetActiveScheme failed with Win32 code {error} (scheme may not exist).");
    }

    private static void Validate(string path)
    {
        if (path != WindowsHostSettingsProvisioner.SchemePath)
            throw new InvalidDataException("Unexpected Windows host setting.");
    }

    [LibraryImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme")]
    private static partial uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr activeSchemeGuid);

    [LibraryImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme")]
    private static partial uint PowerSetActiveScheme(IntPtr rootKey, in Guid schemeGuid);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr memory);
}