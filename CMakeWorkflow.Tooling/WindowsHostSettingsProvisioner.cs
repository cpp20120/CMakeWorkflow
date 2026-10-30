using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling;

// Single strongly typed Windows host setting. A GUID identifies an existing power scheme;
// this provisioner does not create schemes or mutate their individual AC/DC values.
public static class WindowsHostSettingsProvisioner
{
    public const string SchemePath = "windows/power/active-scheme";

    public static IReadOnlyList<HostSettingChange> Plan(HostRequirements requirements, IHostSettingsBackend backend)
    {
        if (requirements.WindowsPowerScheme is not string requested) return [];
        if (!Guid.TryParseExact(requested, "D", out var target))
            throw new InvalidDataException("Windows power scheme must be a canonical GUID.");
        var current = backend.Read(SchemePath);
        if (!Guid.TryParseExact(current, "D", out var before))
            throw new InvalidOperationException("Cannot inspect active Windows power scheme.");
        return before == target ? [] : [new("windows_power_scheme", SchemePath, before.ToString("D"), target.ToString("D"))];
    }

    public static void Apply(IReadOnlyList<HostSettingChange> changes, IHostSettingsBackend backend, CancellationToken ct)
    {
        if (changes.Count > 1 || changes.Any(c => c.Id != "windows_power_scheme" || c.Path != SchemePath ||
            !Guid.TryParseExact(c.Before, "D", out _) || !Guid.TryParseExact(c.After, "D", out _)))
            throw new InvalidDataException("Only the active Windows power scheme may be changed.");
        if (changes.Count == 0) return;
        ct.ThrowIfCancellationRequested();
        var change = changes[0];
        if (backend.Read(SchemePath) != change.Before)
            throw new InvalidOperationException("Windows power scheme changed since approval.");
        ct.ThrowIfCancellationRequested();
        backend.Write(SchemePath, change.After);
        if (backend.Read(SchemePath) != change.After)
            throw new IOException("Windows active power scheme did not retain requested value.");
    }
}

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
        finally { if (allocated != IntPtr.Zero) LocalFree(allocated); }
    }

    public void Write(string path, string value)
    {
        Validate(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Guid.TryParseExact(value, "D", out var scheme)) throw new InvalidDataException("Invalid Windows power scheme GUID.");
        var error = PowerSetActiveScheme(IntPtr.Zero, in scheme);
        if (error != 0) throw new IOException($"PowerSetActiveScheme failed with Win32 code {error} (scheme may not exist).");
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
