using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Tooling.Toolchains;
using CMakeWorkflow.Tooling.Tools;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Environment;
#pragma warning restore IDE0130

public static class HostPreparation
{
    public static HostRequirements ReadRequirements(string path)
    {
        HostRequirements value = JsonSerializer.Deserialize(File.ReadAllText(path), HostPreparationJson.Default.HostRequirements)
                    ?? throw new InvalidDataException("Environment requirements are empty.");
        return value.SchemaVersion != 1 || value.ToolProfiles is not { Length: > 0 } ||
            value.ToolProfiles.Any(string.IsNullOrWhiteSpace) ||
            value.MaxPerfEventParanoid is < -1 or > 4 ||
            (value.CpuGovernor is not null && value.CpuGovernor is not ("performance" or "powersave" or "schedutil")) ||
            (value.WindowsPowerScheme is not null && !Guid.TryParseExact(value.WindowsPowerScheme, "D", out _)) ||
            (value.WindowsPowerScheme is not null &&
            (value.MaxPerfEventParanoid is not null || value.CpuGovernor is not null))
            ? throw new InvalidDataException("Unsupported or invalid environment requirements.")
            : value;
    }

    public static async Task<HostPreparationPlan> InspectAsync(string path, ToolingService service,
        PackageManagerKind packageManager, string? installDirectory, CancellationToken ct,
        IHostSettingsBackend? backend = null, IReadOnlyList<string>? cpuPaths = null,
        IWindowsDevelopmentProbe? windowsProbe = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        string fullPath = Path.GetFullPath(path);
        byte[] content = await File.ReadAllBytesAsync(fullPath, ct);
        HostRequirements requirements = ReadRequirements(fullPath);
        ToolingPlan toolPlan = await service.PlanAsync(requirements.ToolProfiles, packageManager,
            allowFallback: true, installDirectory, ct);
        List<HostFinding> findings = [.. InspectMachine(requirements, backend, cpuPaths)];
        if (requirements.RequireWindowsMsvc || requirements.RequireWindowsSdk || requirements.RequireWindowsWpr)
        {
            if (!OperatingSystem.IsWindows() && windowsProbe is null)
            {
                throw new PlatformNotSupportedException("Windows development requirements require Windows.");
            }

            findings.AddRange(WindowsDevelopmentDoctor.Inspect(requirements,
                windowsProbe ?? new SystemWindowsDevelopmentProbe()));
        }

        if (requirements.WindowsPowerScheme is not null && !OperatingSystem.IsWindows() && backend is null)
        {
            throw new PlatformNotSupportedException("Windows power scheme provisioning requires Windows.");
        }

        if (!OperatingSystem.IsLinux() && backend is null &&
            (requirements.MaxPerfEventParanoid is not null || requirements.CpuGovernor is not null))
        {
            throw new PlatformNotSupportedException("Linux host requirements cannot be provisioned on this platform.");
        }

        string[] policies = requirements.CpuGovernor is null
            ? []
            : [.. (cpuPaths ?? (OperatingSystem.IsLinux()
                ? HostSettingsProvisioner.EnumerateCpuPaths()
                : throw new PlatformNotSupportedException("Linux cpufreq policies unavailable.")))
            .Order(StringComparer.Ordinal)];
        if (policies.Length != policies.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Duplicate CPU policies in host topology.");
        }

        IReadOnlyList<HostSettingChange> systemChanges = requirements.WindowsPowerScheme is not null
            ? WindowsHostSettingsProvisioner.Plan(requirements, backend ?? new WindowsPowerSettingsBackend())
            : OperatingSystem.IsLinux() || backend is not null
                ? HostSettingsProvisioner.Plan(requirements, backend ?? new LinuxHostSettingsBackend(),
                    requirements.CpuGovernor is null ? null : policies)
                : Array.Empty<HostSettingChange>();
        string requirementsHash = Convert.ToHexString(SHA256.HashData(content));
        string planMaterial = requirementsHash + "\n" +
                           JsonSerializer.Serialize(toolPlan, ToolingJson.Default.ToolingPlan) + "\n" +
                           JsonSerializer.Serialize(findings, HostPreparationJson.Default.ListHostFinding) + "\n" +
                           JsonSerializer.Serialize(systemChanges,
                               HostPreparationJson.Default.IReadOnlyListHostSettingChange) + "\n" +
                           JsonSerializer.Serialize(policies, HostPreparationJson.Default.StringArray);
        string approvalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planMaterial)));
        return new(1, requirementsHash, fullPath,
            toolPlan, findings, toolPlan.Ready && findings.All(f => f.Status == "ready"), approvalHash, systemChanges,
            policies);
    }

    public static IReadOnlyList<HostFinding> InspectMachine(HostRequirements requirements,
        IHostSettingsBackend? backend = null, IReadOnlyList<string>? cpuPaths = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        List<HostFinding> findings = [];
        if (requirements.MaxPerfEventParanoid is int maximum)
        {
            const string path = "/proc/sys/kernel/perf_event_paranoid";
            string? observed = backend is null ? ReadLinuxSetting(path) : backend.Read(path);
            bool okay = int.TryParse(observed, out int value) && value <= maximum;
            findings.Add(new("perf_event_paranoid", okay ? "ready" : "drift", observed,
                $"<= {maximum}", OperatingSystem.IsLinux()
                    ? "Review kernel.perf_event_paranoid with your administrator; global sysctl is never changed automatically."
                    : "perf_event_paranoid is Linux-only."));
        }

        if (requirements.CpuGovernor is string expected)
        {
            IReadOnlyList<string> policies = cpuPaths ?? (OperatingSystem.IsLinux() ? HostSettingsProvisioner.EnumerateCpuPaths() : []);
            string?[] values = [.. policies.Select(path => backend is null ? ReadLinuxSetting(path) : backend.Read(path)).Distinct(StringComparer.Ordinal)];
            string? observed = values.Length == 0 ? null : string.Join(",", values);
            findings.Add(new("cpu_governor", values.Length > 0 && values.All(v => v == expected) ? "ready" : "drift",
                observed,
                expected, "CPU governor is a shared host resource and is not modified automatically."));
        }

        if (requirements.WindowsPowerScheme is string expectedScheme)
        {
            string? active = (backend ?? (OperatingSystem.IsWindows() ? new WindowsPowerSettingsBackend() : null))
                ?.Read(WindowsHostSettingsProvisioner.SchemePath);
            string? normalized = Guid.TryParseExact(active, "D", out Guid activeGuid) ? activeGuid.ToString("D") : null;
            findings.Add(new("windows_power_scheme", normalized == Guid.Parse(expectedScheme).ToString("D")
                    ? "ready"
                    : "drift", normalized, Guid.Parse(expectedScheme).ToString("D"),
                "Power scheme is a shared host setting; changes require explicit approval."));
        }

        return findings;
    }

    private static string? ReadLinuxSetting(string path)
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

    public static async Task<HostPreparationResult> ApplyAsync(HostPreparationPlan approved,
        ToolingService service, PackageManagerKind packageManager, string? installDirectory,
        CancellationToken ct, bool allowSystemChanges = false, IHostSettingsBackend? backend = null,
        IReadOnlyList<string>? cpuPaths = null, string? auditDirectory = null,
        IWindowsDevelopmentProbe? windowsProbe = null)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(service);
        // Recompute before any mutation; neither requirements nor planned commands may drift.
        HostPreparationPlan current = await InspectAsync(approved.Source, service, packageManager, installDirectory, ct, backend,
            cpuPaths, windowsProbe);
        if (current.ApprovalSha256 != approved.ApprovalSha256 ||
            current.RequirementsSha256 != approved.RequirementsSha256 ||
            JsonSerializer.Serialize(current.Tools, ToolingJson.Default.ToolingPlan) !=
            JsonSerializer.Serialize(approved.Tools, ToolingJson.Default.ToolingPlan))
        {
            throw new InvalidOperationException("Environment plan is stale; inspect and approve again.");
        }

        IReadOnlyList<HostSettingChange> systemChanges = approved.SystemChanges ?? [];
        if (systemChanges.Count > 0 && !allowSystemChanges)
        {
            throw new InvalidOperationException("System settings require explicit --allow-system-changes approval.");
        }

        if (systemChanges.Count > 0 && backend is null &&
            !((OperatingSystem.IsLinux() && approved.SystemChanges!.All(x => x.Id != "windows_power_scheme")) ||
              (OperatingSystem.IsWindows() && approved.SystemChanges!.All(x => x.Id == "windows_power_scheme"))))
        {
            throw new PlatformNotSupportedException("Host settings do not match the current operating system.");
        }

        ToolingResult? result = approved.Tools.Ready ? null : await service.ExecuteAsync(approved.Tools, ct);
        string? auditPath = null;
        if (systemChanges.Count > 0)
        {
            IHostSettingsBackend target = backend ?? (approved.SystemChanges!.Any(x => x.Id == "windows_power_scheme")
                ? new WindowsPowerSettingsBackend()
                : new LinuxHostSettingsBackend());
            // Record intent before the first non-transactional host write.
            auditPath = WriteAudit(auditDirectory ?? Path.GetDirectoryName(approved.Source)!, approved, "applying",
                null);
            try
            {
                if (systemChanges.Any(x => x.Id == "windows_power_scheme"))
                {
                    WindowsHostSettingsProvisioner.Apply(systemChanges, target, ct);
                }
                else
                {
                    HostSettingsProvisioner.Apply(systemChanges, target, ct);
                }

                _ = WriteAudit(auditDirectory ?? Path.GetDirectoryName(approved.Source)!, approved, "applied", null);
            }
            catch (Exception e)
            {
                _ = WriteAudit(auditDirectory ?? Path.GetDirectoryName(approved.Source)!, approved, "partial_or_failed",
                    e.Message);
                throw;
            }
        }

        HostPreparationPlan checkedPlan = await InspectAsync(approved.Source, service, packageManager, installDirectory, ct, backend,
            cpuPaths, windowsProbe);
        if (systemChanges.Count > 0)
        {
            _ = WriteAudit(auditDirectory ?? Path.GetDirectoryName(approved.Source)!, approved,
                checkedPlan.Ready ? "verified" : "verification_drift", null);
        }

        return new(1, checkedPlan.Ready, result, checkedPlan, systemChanges, auditPath);
    }

    private static string WriteAudit(string directory, HostPreparationPlan plan, string status, string? error)
    {
        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cmakeworkflow-provisioning-audit.jsonl");
        string entry = JsonSerializer.Serialize(new ProvisioningAuditEntry(DateTimeOffset.UtcNow,
                plan.ApprovalSha256, plan.RequirementsSha256, status, plan.SystemChanges ?? [], error),
            HostPreparationJson.Default.ProvisioningAuditEntry);
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using StreamWriter writer = new(stream);
        writer.WriteLine(entry);
        writer.Flush();
        stream.Flush(true);
        return path;
    }
}
