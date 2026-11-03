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
        return before == target
            ? []
            : [new("windows_power_scheme", SchemePath, before.ToString("D"), target.ToString("D"))];
    }

    public static void Apply(IReadOnlyList<HostSettingChange> changes, IHostSettingsBackend backend,
        CancellationToken ct)
    {
        if (changes.Count > 1 || changes.Any(c => c.Id != "windows_power_scheme" || c.Path != SchemePath ||
                                                  !Guid.TryParseExact(c.Before, "D", out _) ||
                                                  !Guid.TryParseExact(c.After, "D", out _)))
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
