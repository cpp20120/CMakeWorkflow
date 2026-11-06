// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Environment;
#pragma warning restore IDE0130

// Single strongly typed Windows host setting. A GUID identifies an existing power scheme;
// this provisioner does not create schemes or mutate their individual AC/DC values.
public static class WindowsHostSettingsProvisioner
{
    public const string SchemePath = "windows/power/active-scheme";

    public static IReadOnlyList<HostSettingChange> Plan(HostRequirements requirements, IHostSettingsBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(requirements);
        if (requirements.WindowsPowerScheme is not string requested)
        {
            return [];
        }

        if (!Guid.TryParseExact(requested, "D", out Guid target))
        {
            throw new InvalidDataException("Windows power scheme must be a canonical GUID.");
        }

        string? current = backend.Read(SchemePath);
        return !Guid.TryParseExact(current, "D", out Guid before)
            ? throw new InvalidOperationException("Cannot inspect active Windows power scheme.")
            : before == target
            ? []
            : [new("windows_power_scheme", SchemePath, before.ToString("D"), target.ToString("D"))];
    }

    public static void Apply(IReadOnlyList<HostSettingChange> changes, IHostSettingsBackend backend,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count > 1 || changes.Any(c => c.Id != "windows_power_scheme" || c.Path != SchemePath ||
                                                  !Guid.TryParseExact(c.Before, "D", out _) ||
                                                  !Guid.TryParseExact(c.After, "D", out _)))
        {
            throw new InvalidDataException("Only the active Windows power scheme may be changed.");
        }

        if (changes.Count == 0)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        HostSettingChange change = changes[0];
        if (backend.Read(SchemePath) != change.Before)
        {
            throw new InvalidOperationException("Windows power scheme changed since approval.");
        }

        ct.ThrowIfCancellationRequested();
        backend.Write(SchemePath, change.After);
        if (backend.Read(SchemePath) != change.After)
        {
            throw new IOException("Windows active power scheme did not retain requested value.");
        }
    }
}
