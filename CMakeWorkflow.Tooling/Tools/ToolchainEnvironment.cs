using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling.Tools;

internal sealed record EnvironmentRequirements(string Profile, ToolchainHost Host, ToolchainTarget Target,
    string Generator, ToolResolutionPolicy Policy, ComponentRequirement[] Versions);

internal sealed record EnvironmentComponent(string Id, string Version, string Origin, ManagedToolRelease Distribution,
    ComponentRequirement[] Dependencies);

internal sealed record ToolchainEnvironmentLock(int SchemaVersion, string Profile, ToolchainHost Host, ToolchainTarget Target,
    string Generator, ToolResolutionPolicy Policy, EnvironmentComponent[] Components);

internal sealed record ToolchainDiagnostic(string Code, string Component, string Message);
internal sealed record EnvironmentInventoryEntry(string Component, string Version, string Origin, string Status, string? Diagnostic);
internal sealed record EnvironmentBinding(string EnvironmentProfileId, string Generator, string HostArchitecture,
    string TargetArchitecture, string EnvironmentLockSha256);
internal sealed record EnvironmentResolution(int SchemaVersion, ToolchainEnvironmentLock? Environment, string? EnvironmentSha256,
    bool Ready, EnvironmentInventoryEntry[] Components, ToolchainDiagnostic[] Diagnostics, EnvironmentBinding? Binding = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ToolchainEnvironmentLock))]
[JsonSerializable(typeof(EnvironmentResolution))]
[JsonSerializable(typeof(ManagedComponent[]))]
internal sealed partial class ToolchainEnvironmentJson : JsonSerializerContext;

internal static class ToolchainEnvironment
{
    internal const string Profile = "linux-clang";
    private static readonly string[] Required = ["cmake", "llvm", "ninja", "sysroot"];

    internal static EnvironmentRequirements Native(string generator, ComponentRequirement[] versions) =>
        new(Profile, ManagedComponentCatalog.LinuxHost, ManagedComponentCatalog.LinuxTarget, generator,
            ToolResolutionPolicy.ManagedOnly, versions);

    internal static (ToolchainEnvironmentLock? Lock, ToolchainDiagnostic? Error) Resolve(
        EnvironmentRequirements request, IReadOnlyList<ManagedComponent> catalog)
    {
        if (request.Profile != Profile || request.Policy != ToolResolutionPolicy.ManagedOnly)
        {
            return (null, new("unsupported-profile-policy", "", "Environment locks require linux-clang with ManagedOnly."));
        }
        if (request.Host != ManagedComponentCatalog.LinuxHost || request.Target != ManagedComponentCatalog.LinuxTarget ||
            request.Generator is not ("Ninja" or "Ninja Multi-Config"))
        {
            return (null, new("unsupported-host-target", "", "linux-clang requires a Linux x64 execution host, GNU x64 target and Ninja."));
        }

        Dictionary<string, string> pins = new(StringComparer.Ordinal);
        foreach (ComponentRequirement version in request.Versions)
        {
            if (!Required.Contains(version.Id, StringComparer.Ordinal))
            {
                return (null, new("unknown-component", version.Id, "Component is not part of the selected profile."));
            }
            if (!pins.TryAdd(version.Id, version.Version))
            {
                return (null, new("conflicting-requirements", version.Id, "Pin each component at most once."));
            }
        }

        Dictionary<string, ManagedComponent> selected = new(StringComparer.Ordinal);
        foreach (string id in Required)
        {
            string? pin = pins.GetValueOrDefault(id);
            ManagedComponent[] candidates = [.. catalog.Where(item => item.Release.Id == id &&
                item.Host == request.Host && (pin is null ? item.ProfileDefault : item.Release.Version == pin) &&
                (item.Targets.Length == 0 || item.Targets.Contains(request.Target)))];
            if (candidates is not [ManagedComponent component])
            {
                return candidates.Length == 0
                    ? (null, new("component-unavailable", id, "Requested release is unavailable for this host and target."))
                    : (null, new("ambiguous-release", id, "The catalog contains multiple matching releases."));
            }
            if (!ValidDistribution(component.Release))
            {
                return (null, new("invalid-catalog-release", id, "The catalog release must have an HTTPS source and valid archive and payload SHA-256 values."));
            }
            selected.Add(id, component);
        }

        foreach ((string id, ManagedComponent component) in selected)
        {
            if (component.Requires.Any(requirement =>
                selected.GetValueOrDefault(requirement.Id)?.Release.Version != requirement.Version))
            {
                return (null, new("incompatible-companions", id, "Resolved companions do not meet this release's exact requirements."));
            }
        }

        return (new(2, Profile, request.Host, request.Target, request.Generator, request.Policy,
            [.. selected.Values.Select(component => new EnvironmentComponent(component.Release.Id,
                component.Release.Version, component.Provider, component.Release, component.Requires))]), null);
    }

    internal static byte[] Serialize(ToolchainEnvironmentLock environment) =>
        JsonSerializer.SerializeToUtf8Bytes(environment, ToolchainEnvironmentJson.Default.ToolchainEnvironmentLock);

    internal static ManagedToolRelease? ReleaseForTool(ToolchainEnvironmentLock environment, string tool) =>
        environment.Components.FirstOrDefault(component =>
            ManagedComponentCatalog.Find(component.Id, component.Version)!.Tools.Contains(tool, StringComparer.Ordinal))
            ?.Distribution;

    private static bool ValidDistribution(ManagedToolRelease release) =>
        release.Sha256 is { Length: 64 } && release.TreeSha256 is { Length: 64 } &&
        release.Sha256.All(char.IsAsciiHexDigitLower) && release.TreeSha256.All(char.IsAsciiHexDigitLower) &&
        Uri.TryCreate(release.Source, UriKind.Absolute, out Uri? source) &&
        source.Scheme == Uri.UriSchemeHttps && source.UserInfo.Length == 0 && source.Query.Length == 0 && source.Fragment.Length == 0;

    internal static ToolchainEnvironmentLock Validate(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 2)
        {
            throw new InvalidDataException("Unsupported toolchain environment lock schema.");
        }

        ToolchainEnvironmentLock? input = JsonSerializer.Deserialize(bytes, ToolchainEnvironmentJson.Default.ToolchainEnvironmentLock);
        if (input?.Components is not { Length: 4 } components ||
            components.Any(component => component is null || component.Id is null || component.Version is null))
        {
            throw new InvalidDataException("Invalid toolchain environment components.");
        }

        (ToolchainEnvironmentLock? expected, ToolchainDiagnostic? error) = Resolve(
            new(input.Profile, input.Host, input.Target, input.Generator, input.Policy,
                [.. components.Select(component => new ComponentRequirement(component.Id, component.Version))]),
            ManagedComponentCatalog.Components);
        return expected is not null && bytes.AsSpan().SequenceEqual(Serialize(expected))
            ? expected
            : throw new InvalidDataException(error?.Message ?? "Toolchain environment lock differs from the canonical catalog identities.");
    }

    internal static async Task<EnvironmentInventoryEntry> InspectAsync(string store, ManagedToolRelease release, CancellationToken ct)
    {
        const string origin = "managed-release";
        try
        {
            bool present = await ManagedToolStore.IsInstalledAsync(store, release, ct);
            return new(release.Id, release.Version, origin, present ? "installed" : "missing", null);
        }
        catch (InvalidDataException error)
        {
            return new(release.Id, release.Version, origin, "corrupted", error.Message);
        }
    }

    internal static async Task<EnvironmentResolution> InspectAsync(EnvironmentRequirements request, CancellationToken ct)
    {
        (ToolchainEnvironmentLock? environment, ToolchainDiagnostic? error) = Resolve(request, ManagedComponentCatalog.Components);
        if (environment is null)
        {
            return new(1, null, null, false, [], [error!]);
        }

        string store = ManagedToolStore.GetRoot();
        List<EnvironmentInventoryEntry> inventory = [];
        List<ToolchainDiagnostic> diagnostics = [];
        foreach (EnvironmentComponent component in environment.Components)
        {
            EnvironmentInventoryEntry state = await InspectAsync(store, component.Distribution, ct);
            inventory.Add(state);
            if (state.Status != "installed")
            {
                diagnostics.Add(new(state.Status, state.Component, state.Diagnostic ?? "Explicit provisioning is required."));
            }
        }
        string digest = ManagedEnvironmentIdentity.Hash(Serialize(environment));
        bool ready = diagnostics.Count == 0;
        return new(1, environment, digest, ready, [.. inventory], [.. diagnostics],
            ready ? new("linux-managed-clang", environment.Generator, environment.Host.Architecture,
                environment.Target.Architecture, digest) : null);
    }

    internal static ToolchainHost ExecutionHost() => new(
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "macos",
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" :
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "unsupported");
}
