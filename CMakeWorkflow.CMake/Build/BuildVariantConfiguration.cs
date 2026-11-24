using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Build;

internal enum VariantConfiguration { Debug, Release, RelWithDebInfo, MinSizeRel }
internal enum VariantLinkage { Default, Static, Shared }
internal enum VariantOptimization { Default, None, Speed, Size }
internal enum VariantSwitch { Default, Enabled, Disabled }
internal enum VariantInstrumentation { None, Address, Undefined, AddressUndefined, Thread }

internal sealed record VariantSettings(
    VariantConfiguration Configuration = VariantConfiguration.Release,
    VariantLinkage Linkage = VariantLinkage.Default,
    VariantOptimization Optimization = VariantOptimization.Default,
    VariantSwitch DebugInformation = VariantSwitch.Default,
    VariantInstrumentation Instrumentation = VariantInstrumentation.None,
    VariantSwitch Lto = VariantSwitch.Default,
    bool Testing = true);

internal sealed record VariantEnvironment(string HostOs, string HostArchitecture, string TargetOs,
    string TargetArchitecture, string Abi, string Generator, string? EnvironmentLockSha256);

internal sealed record VariantIdentity(string EvaluatorVersion, string SdkSha256, string ProjectType,
    IReadOnlyDictionary<string, string> Inputs, VariantSettings Effective, VariantEnvironment Environment);

internal sealed record EvaluatedVariant(string Name, string Id, VariantSettings Effective, VariantEnvironment Environment,
    IReadOnlyDictionary<string, string> Origins);

internal sealed record VariantLock(int SchemaVersion, string EvaluatorVersion, string SdkSha256, string ProjectType,
    IReadOnlyDictionary<string, string> Inputs, EvaluatedVariant[] Variants);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(VariantIdentity))]
[JsonSerializable(typeof(VariantLock))]
internal sealed partial class VariantJson : JsonSerializerContext;

internal static class BuildVariantConfiguration
{
    internal const string Version = "1.0.0";
    internal const string Store = "cmwf-config";
    internal const string Module = "cmake/cmwf-config/v1/Variant.cmake";
    internal const string SdkLock = "cmwf.sdk.lock.json";

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    internal static string LockPath(string digest) => Store + "/" + digest + "/lock.json";

    internal static string Identity(VariantLock plan, VariantSettings settings, VariantEnvironment environment) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new VariantIdentity(plan.EvaluatorVersion, plan.SdkSha256,
            plan.ProjectType, plan.Inputs, settings, environment), VariantJson.Default.VariantIdentity));

    internal static string BuildDirectory(EvaluatedVariant variant) =>
        "out/build/" + variant.Effective.Configuration + "-" + variant.Id;

    internal static WorkflowToolchainBinding? Toolchain(VariantEnvironment environment) =>
        environment.EnvironmentLockSha256 is { } digest ? new(EnvironmentProfileId: "linux-managed-clang",
            Generator: environment.Generator, HostArchitecture: environment.HostArchitecture,
            TargetArchitecture: environment.TargetArchitecture)
        { EnvironmentLockSha256 = digest } : null;

    internal static void MatchContext(EvaluatedVariant variant, WorkflowToolchainBinding? toolchain, string? configuration, string? generator)
    {
        if (toolchain != Toolchain(variant.Environment) || generator != variant.Environment.Generator ||
            configuration != variant.Effective.Configuration.ToString())
        {
            throw new InvalidDataException("variant-context-mismatch: configuration, generator or toolchain differs from the lock.");
        }
    }

    internal static void Validate(VariantSettings settings, VariantEnvironment environment, string projectType)
    {
        if (!Enum.IsDefined(settings.Configuration) || !Enum.IsDefined(settings.Linkage) ||
            !Enum.IsDefined(settings.Optimization) || !Enum.IsDefined(settings.DebugInformation) ||
            !Enum.IsDefined(settings.Instrumentation) || !Enum.IsDefined(settings.Lto))
        {
            throw new InvalidDataException("invalid-setting: unknown build variant value.");
        }

        if (projectType is not ("Native.Library" or "Native.Application") ||
            (projectType == "Native.Application" && settings.Linkage != VariantLinkage.Default))
        {
            throw new InvalidDataException("unsupported-linkage: explicit linkage requires Native.Library.");
        }

        bool ambient = environment.EnvironmentLockSha256 is null;
        bool supported = ambient
            ? environment is
            {
                HostOs: "native", HostArchitecture: "native", TargetOs: "native",
                TargetArchitecture: "native", Abi: "ambient", Generator: "Ninja"
            }
            : IsDigest(environment.EnvironmentLockSha256) &&
              environment is
              {
                  HostOs: "linux", HostArchitecture: "x64", TargetOs: "linux",
                  TargetArchitecture: "x64", Abi: "gnu", Generator: "Ninja" or "Ninja Multi-Config"
              };
        if (!supported)
        {
            throw new InvalidDataException("incompatible-environment: target, ABI or generator differs from the selected environment.");
        }

        if (settings.Instrumentation == VariantInstrumentation.Thread ||
            (!ambient && settings.Instrumentation != VariantInstrumentation.None))
        {
            throw new InvalidDataException("unsupported-instrumentation: the selected profile has no admitted sanitizer runtime.");
        }

        if (settings.Instrumentation != VariantInstrumentation.None && settings.Lto == VariantSwitch.Enabled)
        {
            throw new InvalidDataException("incompatible-instrumentation: IPO and sanitizer instrumentation cannot be combined.");
        }
    }

    internal static string CheckedPath(string root, string relative, Action<string>? readGuard = null)
    {
        if (new DirectoryInfo(root).LinkTarget is not null)
        {
            throw new InvalidDataException("unsafe-input-link: project root");
        }
        string path = root;
        foreach (string segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment.EndsWith('.') || IsDeviceName(segment) ||
                segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            {
                throw new InvalidDataException("unsafe-input-path: " + relative);
            }

            path = Path.Combine(path, segment);
            FileInfo info = new(path);
            if (info.LinkTarget is not null || (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException("unsafe-input-link: " + relative);
            }
        }

        readGuard?.Invoke(path);
        return path;
    }

    private static bool IsDeviceName(string segment)
    {
        string stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
               (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                     stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]));
    }

    internal static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        if (stream.Length > 1024 * 1024)
        {
            throw new InvalidDataException("configuration-input-too-large: " + Path.GetFileName(path));
        }

        byte[] bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        return stream.ReadByte() == -1 ? bytes : throw new InvalidDataException("configuration-input-changed");
    }

    internal static async Task<EvaluatedVariant> LoadAsync(string source, WorkflowVariantBinding binding, CancellationToken ct,
        Action<string>? readGuard = null)
    {
        if (!IsDigest(binding.Id) || !IsDigest(binding.LockSha256))
        {
            throw new InvalidDataException("invalid-variant-binding: expected lowercase SHA-256 values.");
        }

        byte[] bytes = await ReadBoundedAsync(CheckedPath(source, LockPath(binding.LockSha256), readGuard), ct);
        if (Hash(bytes) != binding.LockSha256)
        {
            throw new InvalidDataException("configuration-lock-changed: approved SHA-256 does not match.");
        }

        VariantLock? plan = JsonSerializer.Deserialize(bytes, VariantJson.Default.VariantLock);
        if (plan is not { SchemaVersion: 1, EvaluatorVersion: Version, Inputs.Count: > 0 and <= 144, Variants.Length: > 0 and <= 32 } ||
            !IsDigest(plan.SdkSha256) || !plan.Inputs.ContainsKey(Module) ||
            plan.Inputs.GetValueOrDefault(SdkLock) != plan.SdkSha256 ||
            !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(plan, VariantJson.Default.VariantLock)))
        {
            throw new InvalidDataException("invalid-configuration-lock: invalid schema or noncanonical evidence.");
        }

        EvaluatedVariant selected = plan.Variants.SingleOrDefault(item => item?.Id == binding.Id)
            ?? throw new InvalidDataException("unknown-variant: ID is absent from approved configuration lock.");
        if (selected.Effective is null || selected.Environment is null ||
            Identity(plan, selected.Effective, selected.Environment) != binding.Id)
        {
            throw new InvalidDataException("variant-identity-changed: configuration evidence does not match its ID.");
        }

        Validate(selected.Effective, selected.Environment, plan.ProjectType);
        foreach ((string path, string digest) in plan.Inputs)
        {
            if (Hash(await ReadBoundedAsync(CheckedPath(source, path, readGuard), ct)) != digest)
            {
                throw new InvalidDataException("configuration-input-changed: " + path);
            }
        }

        return selected;
    }

    internal static string[] CacheArguments(EvaluatedVariant variant)
    {
        VariantSettings settings = variant.Effective;
        string configurationVariable = variant.Environment.Generator == "Ninja Multi-Config"
            ? "CMAKE_CONFIGURATION_TYPES" : "CMAKE_BUILD_TYPE";
        return
        [
            "-DCMWF_VARIANT_ID:STRING=" + variant.Id,
            "-DCMWF_VARIANT_LINKAGE:STRING=" + settings.Linkage,
            "-DCMWF_VARIANT_OPTIMIZATION:STRING=" + settings.Optimization,
            "-DCMWF_VARIANT_DEBUG:STRING=" + settings.DebugInformation,
            "-DCMWF_VARIANT_INSTRUMENTATION:STRING=" + settings.Instrumentation,
            "-DCMWF_VARIANT_LTO:STRING=" + settings.Lto,
            "-DBUILD_TESTING:BOOL=" + (settings.Testing ? "ON" : "OFF"),
            "-D" + configurationVariable + ":STRING=" + settings.Configuration
        ];
    }

    internal static void VerifyTree(string tree, EvaluatedVariant variant, bool required = false)
    {
        string cache = Path.Combine(tree, "CMakeCache.txt");
        if (new FileInfo(cache).LinkTarget is not null)
        {
            throw new InvalidDataException("unsafe-variant-cache: the configured cache must not be a link.");
        }
        if (!required && !File.Exists(cache))
        {
            return;
        }

        HashSet<string> entries = new(File.ReadLines(cache), StringComparer.Ordinal);
        if (!entries.Contains("CMWF_VARIANT_APPLIED:INTERNAL=" + variant.Id) ||
            CacheArguments(variant).Any(arg => !entries.Contains(arg[2..])))
        {
            throw new InvalidDataException("configured-variant-changed: CMake tree does not carry its approved build policy.");
        }
    }
}
