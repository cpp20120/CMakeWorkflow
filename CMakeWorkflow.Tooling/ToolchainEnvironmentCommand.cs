using System.Text;
using System.Text.Json;
using CMakeWorkflow.Tooling.Toolchains;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Tooling;

internal static class ToolchainEnvironmentCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args is ["catalog"] or ["catalog", "--json"])
        {
            await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(CatalogBytes()));
            return 0;
        }
        if (args is ["installed"] or ["installed", "--json"])
        {
            return await InstalledAsync(ct);
        }
        if (args is ["verify", "--environment-lock", _, "--json"])
        {
            args = args[..^1];
        }
        if (args is ["verify", "--environment-lock", string digest])
        {
            ManagedLinuxSdk.RequireHostAbi();
            ToolchainEnvironmentLock? verified = await ManagedEnvironmentLocks.ReadAsync(digest, ct);
            EnvironmentRequirements request = verified is null
                ? ToolchainEnvironment.Native("Ninja Multi-Config", [])
                : new(verified.Profile, verified.Host, verified.Target, verified.Generator, verified.Policy,
                    [.. verified.Components.Select(component => new ComponentRequirement(component.Id, component.Version))]);
            EnvironmentResolution result = await ToolchainEnvironment.InspectAsync(request, ct);
            await PrintAsync(result with
            {
                EnvironmentSha256 = digest,
                Environment = verified,
                Binding = result.Binding is { } binding ? binding with { EnvironmentLockSha256 = digest } : null
            });
            return result.Ready ? 0 : 2;
        }

        if (args[0] is not ("resolve" or "lock"))
        {
            throw new ArgumentException("Usage: tooling catalog | installed | resolve [options] | lock --profile linux-clang [options] | verify --environment-lock SHA256");
        }

        ToolchainHost host = ToolchainEnvironment.ExecutionHost();
        ToolchainTarget target = ManagedComponentCatalog.LinuxTarget;
        string profile = ToolchainEnvironment.Profile;
        string generator = "Ninja Multi-Config";
        ToolResolutionPolicy policy = ToolResolutionPolicy.ManagedOnly;
        string? approval = null;
        List<ComponentRequirement> versions = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string flag = args[i];
            if (flag == "--json" && seen.Add(flag))
            {
                continue;
            }
            if (i + 1 == args.Length || (!seen.Add(flag) && flag != "--version"))
            {
                throw new ArgumentException("Missing or repeated tooling option: " + flag);
            }

            string value = args[++i];
            switch (flag)
            {
                case "--profile": profile = value; break;
                case "--generator": generator = value; break;
                case "--target-os": target = target with { Os = value }; break;
                case "--target-arch": target = target with { Architecture = value }; break;
                case "--target-abi": target = target with { Abi = value }; break;
                case "--target-triple": target = target with { Triple = value }; break;
                case "--tool-policy" when Enum.TryParse(value, ignoreCase: false, out ToolResolutionPolicy selected) &&
                                               selected.ToString() == value:
                    policy = selected;
                    break;
                case "--version": versions.Add(ParseVersion(value)); break;
                case "--approve-sha" when args[0] == "lock": approval = value; break;
                default: throw new ArgumentException("Unsupported tooling option: " + flag);
            }
        }

        if (host == ManagedComponentCatalog.LinuxHost)
        {
            ManagedLinuxSdk.RequireHostAbi();
        }
        EnvironmentResolution resolution = args[0] == "resolve" && host == ManagedComponentCatalog.LinuxHost &&
            target == ManagedComponentCatalog.LinuxTarget && profile == "cmake-ninja" && versions.Count == 0 &&
            generator is "Ninja" or "Ninja Multi-Config" && policy == ToolResolutionPolicy.AllowSystem
                ? await ResolveSystemToolsAsync(ct)
                : await ToolchainEnvironment.InspectAsync(new(profile, host, target, generator, policy, [.. versions]), ct);
        if (approval is not null)
        {
            if (resolution.Environment is null || !resolution.Ready || resolution.EnvironmentSha256 != approval)
            {
                throw new InvalidDataException("The environment is not verified or differs from the approved resolution SHA-256.");
            }
            await ManagedEnvironmentLocks.WriteAsync(ToolchainEnvironment.Serialize(resolution.Environment), approval, ct);
        }
        await PrintAsync(resolution);
        return resolution.Ready ? 0 : 2;
    }

    internal static ComponentRequirement ParseVersion(string value)
    {
        int boundary = value.IndexOf('=', StringComparison.Ordinal);
        return boundary > 0 && boundary < value.Length - 1
            ? new(value[..boundary], value[(boundary + 1)..])
            : throw new ArgumentException("Expected --version COMPONENT=EXACT_VERSION.");
    }

    private static byte[] CatalogBytes()
    {
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WritePropertyName("components");
            JsonSerializer.Serialize(writer, [.. ManagedComponentCatalog.Components], ToolchainEnvironmentJson.Default.ManagedComponentArray);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static async Task<int> InstalledAsync(CancellationToken ct)
    {
        ToolchainHost host = ToolchainEnvironment.ExecutionHost();
        List<EnvironmentInventoryEntry> components = [];
        if (host == ManagedComponentCatalog.LinuxHost)
        {
            string store = ManagedToolStore.GetRoot();
            foreach (ManagedComponent component in ManagedComponentCatalog.Components)
            {
                components.Add(await ToolchainEnvironment.InspectAsync(store, component.Release, ct));
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            ToolchainInventory inventory = await WindowsToolchains.DiscoverAsync(ct);
            foreach (ToolchainCandidate candidate in inventory.Candidates.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                components.Add(new(candidate.Id, candidate.Version ?? "", "system", "discovered", candidate.Role + " " + candidate.Flavor));
            }
        }

        await PrintAsync(new(1, null, null, false, [.. components], []));
        return 0;
    }

    private static async Task<EnvironmentResolution> ResolveSystemToolsAsync(CancellationToken ct)
    {
        ToolingService service = new(new SystemToolingRuntime(), new VerifiedCMakeInstaller());
        ToolingPlan plan = await service.PlanAsync(["cmake-ninja"], PackageManagerKind.None, false, null, ct,
            resolutionPolicy: ToolResolutionPolicy.AllowSystem);
        return new(1, null, null, plan.Ready,
            [.. plan.Tools.Select(tool => new EnvironmentInventoryEntry(tool.Id, tool.Version ?? "", tool.Origin ?? "unknown",
                tool.Installed ? "installed" : tool.Resolution ?? "missing", tool.Diagnostic))],
            [new("local-build-tools", "", "This inventory covers build tools on the execution host. It does not define a portable compiler environment.")]);
    }

    private static Task PrintAsync(EnvironmentResolution resolution) => Console.Out.WriteLineAsync(
        JsonSerializer.Serialize(resolution, ToolchainEnvironmentJson.Default.EnvironmentResolution));
}
