using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace CMakeWorkflow.Tooling;

// Deliberately separate machine observations from mutating tool installation.
// A plan never modifies global kernel or CPU settings.
public sealed record HostRequirements(int SchemaVersion, string[] ToolProfiles,
    int? MaxPerfEventParanoid = null, string? CpuGovernor = null,
    string? WindowsPowerScheme = null,
    bool RequireWindowsMsvc = false, bool RequireWindowsSdk = false, bool RequireWindowsWpr = false);
public sealed record HostFinding(string Id, string Status, string? Observed, string Expected,
    string? Diagnostic);
public sealed record HostPreparationPlan(int SchemaVersion, string RequirementsSha256,
    string Source, ToolingPlan Tools, IReadOnlyList<HostFinding> Findings, bool Ready, string ApprovalSha256,
    IReadOnlyList<HostSettingChange>? SystemChanges = null,
    IReadOnlyList<string>? CpuPolicyPaths = null);
public sealed record HostPreparationResult(int SchemaVersion, bool Ready,
    ToolingResult? AppliedTools, HostPreparationPlan Checked,
    IReadOnlyList<HostSettingChange>? AppliedSystemChanges = null, string? AuditPath = null);

public static class HostPreparation
{


    public static HostRequirements ReadRequirements(string path)
    {
        var value = JsonSerializer.Deserialize(File.ReadAllText(path), HostPreparationJson.Default.HostRequirements)
            ?? throw new InvalidDataException("Environment requirements are empty.");
        if (value.SchemaVersion != 1 || value.ToolProfiles is not { Length: > 0 } ||
            value.ToolProfiles.Any(string.IsNullOrWhiteSpace) ||
            value.MaxPerfEventParanoid is < -1 or > 4 ||
            value.CpuGovernor is not null && value.CpuGovernor is not ("performance" or "powersave" or "schedutil") ||
            value.WindowsPowerScheme is not null && !Guid.TryParseExact(value.WindowsPowerScheme, "D", out _) ||
            value.WindowsPowerScheme is not null && (value.MaxPerfEventParanoid is not null || value.CpuGovernor is not null))
            throw new InvalidDataException("Unsupported or invalid environment requirements.");
        return value;
    }

    public static async Task<HostPreparationPlan> InspectAsync(string path, ToolingService service,
        PackageManagerKind packageManager, string? installDirectory, CancellationToken ct,
        IHostSettingsBackend? backend = null, IReadOnlyList<string>? cpuPaths = null,
        IWindowsDevelopmentProbe? windowsProbe = null)
    {
        var fullPath = Path.GetFullPath(path);
        var content = await File.ReadAllBytesAsync(fullPath, ct);
        var requirements = ReadRequirements(fullPath);
        var toolPlan = await service.PlanAsync(requirements.ToolProfiles, packageManager,
            allowFallback: true, installDirectory, ct);
        var findings = InspectMachine(requirements, backend, cpuPaths).ToList();
        if (requirements.RequireWindowsMsvc || requirements.RequireWindowsSdk || requirements.RequireWindowsWpr)
        {
            if (!OperatingSystem.IsWindows() && windowsProbe is null)
                throw new PlatformNotSupportedException("Windows development requirements require Windows.");
            findings.AddRange(WindowsDevelopmentDoctor.Inspect(requirements,
                windowsProbe ?? new SystemWindowsDevelopmentProbe()));
        }
        if (requirements.WindowsPowerScheme is not null && !OperatingSystem.IsWindows() && backend is null)
            throw new PlatformNotSupportedException("Windows power scheme provisioning requires Windows.");
        if (!OperatingSystem.IsLinux() && backend is null &&
            (requirements.MaxPerfEventParanoid is not null || requirements.CpuGovernor is not null))
            throw new PlatformNotSupportedException("Linux host requirements cannot be provisioned on this platform.");
        var policies = requirements.CpuGovernor is null ? Array.Empty<string>() :
            (cpuPaths ?? (OperatingSystem.IsLinux() ? HostSettingsProvisioner.EnumerateCpuPaths() :
                throw new PlatformNotSupportedException("Linux cpufreq policies unavailable.")))
            .Order(StringComparer.Ordinal).ToArray();
        if (policies.Length != policies.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException("Duplicate CPU policies in host topology.");
        var systemChanges = requirements.WindowsPowerScheme is not null
            ? WindowsHostSettingsProvisioner.Plan(requirements, backend ?? new WindowsPowerSettingsBackend())
            : OperatingSystem.IsLinux() || backend is not null
                ? HostSettingsProvisioner.Plan(requirements, backend ?? new LinuxHostSettingsBackend(),
                    requirements.CpuGovernor is null ? null : policies)
                : Array.Empty<HostSettingChange>();
        var requirementsHash = Convert.ToHexString(SHA256.HashData(content));
        var planMaterial = requirementsHash + "\n" +
            JsonSerializer.Serialize(toolPlan, ToolingJson.Default.ToolingPlan) + "\n" +
            JsonSerializer.Serialize(findings, HostPreparationJson.Default.ListHostFinding) + "\n" +
            JsonSerializer.Serialize(systemChanges, HostPreparationJson.Default.IReadOnlyListHostSettingChange) + "\n" +
            JsonSerializer.Serialize(policies, HostPreparationJson.Default.StringArray);
        var approvalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(planMaterial)));
        return new(1, requirementsHash, fullPath,
            toolPlan, findings, toolPlan.Ready && findings.All(f => f.Status == "ready"), approvalHash, systemChanges, policies);
    }

    public static IReadOnlyList<HostFinding> InspectMachine(HostRequirements requirements,
        IHostSettingsBackend? backend = null, IReadOnlyList<string>? cpuPaths = null)
    {
        var findings = new List<HostFinding>();
        if (requirements.MaxPerfEventParanoid is int maximum)
        {
            const string path = "/proc/sys/kernel/perf_event_paranoid";
            var observed = backend is null ? ReadLinuxSetting(path) : backend.Read(path);
            var okay = int.TryParse(observed, out var value) && value <= maximum;
            findings.Add(new("perf_event_paranoid", okay ? "ready" : "drift", observed,
                $"<= {maximum}", OperatingSystem.IsLinux()
                    ? "Review kernel.perf_event_paranoid with your administrator; global sysctl is never changed automatically."
                    : "perf_event_paranoid is Linux-only."));
        }
        if (requirements.CpuGovernor is string expected)
        {
            var policies = cpuPaths ?? (OperatingSystem.IsLinux() ? HostSettingsProvisioner.EnumerateCpuPaths() : []);
            var values = policies.Select(path => backend is null ? ReadLinuxSetting(path) : backend.Read(path))
                .Distinct(StringComparer.Ordinal).ToArray();
            var observed = values.Length == 0 ? null : string.Join(",", values);
            findings.Add(new("cpu_governor", values.Length > 0 && values.All(v => v == expected) ? "ready" : "drift", observed,
                expected, "CPU governor is a shared host resource and is not modified automatically."));
        }
        if (requirements.WindowsPowerScheme is string expectedScheme)
        {
            var active = (backend ?? (OperatingSystem.IsWindows() ? new WindowsPowerSettingsBackend() : null))
                ?.Read(WindowsHostSettingsProvisioner.SchemePath);
            var normalized = Guid.TryParseExact(active, "D", out var activeGuid) ? activeGuid.ToString("D") : null;
            findings.Add(new("windows_power_scheme", normalized == Guid.Parse(expectedScheme).ToString("D")
                ? "ready" : "drift", normalized, Guid.Parse(expectedScheme).ToString("D"),
                "Power scheme is a shared host setting; changes require explicit approval."));
        }
        return findings;
    }

    private static string? ReadLinuxSetting(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static async Task<HostPreparationResult> ApplyAsync(HostPreparationPlan approved,
        ToolingService service, PackageManagerKind packageManager, string? installDirectory,
        CancellationToken ct, bool allowSystemChanges = false, IHostSettingsBackend? backend = null,
        IReadOnlyList<string>? cpuPaths = null, string? auditDirectory = null,
        IWindowsDevelopmentProbe? windowsProbe = null)
    {
        // Recompute before any mutation; neither requirements nor planned commands may drift.
        var current = await InspectAsync(approved.Source, service, packageManager, installDirectory, ct, backend, cpuPaths, windowsProbe);
        if (current.ApprovalSha256 != approved.ApprovalSha256 ||
            current.RequirementsSha256 != approved.RequirementsSha256 ||
            JsonSerializer.Serialize(current.Tools, ToolingJson.Default.ToolingPlan) !=
            JsonSerializer.Serialize(approved.Tools, ToolingJson.Default.ToolingPlan))
            throw new InvalidOperationException("Environment plan is stale; inspect and approve again.");
        var systemChanges = approved.SystemChanges ?? [];
        if (systemChanges.Count > 0 && !allowSystemChanges)
            throw new InvalidOperationException("System settings require explicit --allow-system-changes approval.");
        if (systemChanges.Count > 0 && backend is null &&
            !(OperatingSystem.IsLinux() && approved.SystemChanges!.All(x => x.Id != "windows_power_scheme") ||
              OperatingSystem.IsWindows() && approved.SystemChanges!.All(x => x.Id == "windows_power_scheme")))
            throw new PlatformNotSupportedException("Host settings do not match the current operating system.");
        var result = approved.Tools.Ready ? null : await service.ExecuteAsync(approved.Tools, ct);
        string? auditPath = null;
        if (systemChanges.Count > 0)
        {
            var target = backend ?? (approved.SystemChanges!.Any(x => x.Id == "windows_power_scheme")
                ? new WindowsPowerSettingsBackend() : new LinuxHostSettingsBackend());
            // Record intent before the first non-transactional host write.
            auditPath = WriteAudit(auditDirectory ?? Path.GetDirectoryName(approved.Source)!, approved, "applying", null);
            try
            {
                if (systemChanges.Any(x => x.Id == "windows_power_scheme"))
                    WindowsHostSettingsProvisioner.Apply(systemChanges, target, ct);
                else HostSettingsProvisioner.Apply(systemChanges, target, ct);
                WriteAudit(auditDirectory ?? Path.GetDirectoryName(approved.Source)!, approved, "applied", null);
            }
            catch (Exception e)
            {
                WriteAudit(auditDirectory ?? Path.GetDirectoryName(approved.Source)!, approved, "partial_or_failed", e.Message);
                throw;
            }
        }
        var checkedPlan = await InspectAsync(approved.Source, service, packageManager, installDirectory, ct, backend, cpuPaths, windowsProbe);
        if (systemChanges.Count > 0)
            WriteAudit(auditDirectory ?? Path.GetDirectoryName(approved.Source)!, approved,
                checkedPlan.Ready ? "verified" : "verification_drift", null);
        return new(1, checkedPlan.Ready, result, checkedPlan, systemChanges, auditPath);
    }
    private static string WriteAudit(string directory, HostPreparationPlan plan, string status, string? error)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cmakeworkflow-provisioning-audit.jsonl");
        var entry = JsonSerializer.Serialize(new ProvisioningAuditEntry(DateTimeOffset.UtcNow,
            plan.ApprovalSha256, plan.RequirementsSha256, status, plan.SystemChanges ?? [], error),
            HostPreparationJson.Default.ProvisioningAuditEntry);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(entry);
        writer.Flush();
        stream.Flush(true);
        return path;
    }
}

public sealed record ProvisioningAuditEntry(DateTimeOffset At, string ApprovalSha256,
    string RequirementsSha256, string Status, IReadOnlyList<HostSettingChange> Changes, string? Error);

public static class EnvironmentCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        try
        {
            if (args.Length < 2 || args[0] is not ("doctor" or "plan" or "setup"))
                throw new ArgumentException("env <doctor|plan|setup> <requirements.json> [--yes] [--json]");
            var action = args[0]; var source = args[1];
            var yes = false; var json = false; var allowSystemChanges = false; string? expectedPlan = null;
            for (var i = 2; i < args.Length; i++)
                switch (args[i])
                {
                    case "--yes": yes = true; break;
                    case "--json": json = true; break;
                    case "--allow-system-changes": allowSystemChanges = true; break;
                    case "--plan-sha" when i + 1 < args.Length:
                        expectedPlan = args[++i]; break;
                    default: throw new ArgumentException($"Unknown environment option: {args[i]}");
                }
            var service = new ToolingService(new SystemToolingRuntime(), new VerifiedCMakeInstaller());
            var plan = await HostPreparation.InspectAsync(source, service, PackageManagerKind.Auto, null, ct);
            if (action != "setup")
            {
                if (json) Console.WriteLine(JsonSerializer.Serialize(plan, HostPreparationJson.Default.HostPreparationPlan));
                else Print(plan);
                return action == "doctor" && !plan.Ready ? 2 : 0;
            }
            if (!yes || expectedPlan is null ||
                !string.Equals(expectedPlan, plan.ApprovalSha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Review env plan and pass --yes --plan-sha <approvalSha256> for that exact plan.");
            if (!json) Print(plan);
            var result = await HostPreparation.ApplyAsync(plan, service, PackageManagerKind.Auto, null, ct,
                allowSystemChanges);
            if (json) Console.WriteLine(JsonSerializer.Serialize(result, HostPreparationJson.Default.HostPreparationResult));
            else { Console.WriteLine(result.Ready ? "Environment ready." : "Environment still has drift."); Print(result.Checked); }
            return result.Ready ? 0 : 2;
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException
            or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Console.Error.WriteLine("[CMakeWorkflow] Environment: " + e.Message);
            return 1;
        }
    }

    private static void Print(HostPreparationPlan plan)
    {
        Console.WriteLine($"Requirements: {plan.Source}\nSHA256: {plan.RequirementsSha256}\nApproval SHA256: {plan.ApprovalSha256}");
        foreach (var tool in plan.Tools.Tools)
            Console.WriteLine($"Tool {tool.Id}: {(tool.Installed ? "ready" : "missing")}");
        foreach (var step in plan.Tools.Steps)
            Console.WriteLine($"Plan: {step.Tool} {step.Method} {step.Executable} {string.Join(" ", step.Arguments)}");
        foreach (var change in plan.SystemChanges ?? [])
            Console.WriteLine($"System plan: {change.Id} {change.Path}: {change.Before} -> {change.After}");
        foreach (var finding in plan.Findings)
            Console.WriteLine($"Host {finding.Id}: {finding.Status} ({finding.Observed ?? "unavailable"}; expected {finding.Expected}) — {finding.Diagnostic}");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    Converters = [typeof(JsonStringEnumConverter<ToolPlatform>),
        typeof(JsonStringEnumConverter<PackageManagerKind>)])]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(HostRequirements))]
[JsonSerializable(typeof(List<HostFinding>))]
[JsonSerializable(typeof(HostPreparationPlan))]
[JsonSerializable(typeof(HostPreparationResult))]
[JsonSerializable(typeof(IReadOnlyList<HostSettingChange>))]
[JsonSerializable(typeof(ProvisioningAuditEntry))]
internal partial class HostPreparationJson : JsonSerializerContext;
