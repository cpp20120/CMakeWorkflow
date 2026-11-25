namespace CMakeWorkflow.Contracts.Kits;

/// <summary>Execution or target platform requirements independent of placement.</summary>
public sealed record KitPlatform(string Os, string Architecture, string? Abi = null);

public enum KitProvider { ManagedClang, AmbientGcc, AmbientClang, Msvc, AppleClang }

public sealed record KitComponent(string Id, string Version);

/// <summary>Backend-specific CMake settings, kept outside the platform requirements.</summary>
public sealed record KitCMakeSettings(string Generator);

public sealed record EngineeringKitDefinition(int SchemaVersion, string Id, string DisplayName, string Backend,
    KitProvider Provider, KitPlatform ExecutionHost, KitPlatform Target, [property: System.Text.Json.Serialization.JsonPropertyName("cmake")] KitCMakeSettings CMake)
{
    public IReadOnlyList<KitComponent> Components { get; init; } = [];
}

/// <summary>Portable requirements approved for execution. Host observations are recorded separately.</summary>
public sealed record EngineeringKitLock(int SchemaVersion, KitProvider Provider, KitPlatform ExecutionHost,
    KitPlatform Target, string Backend, [property: System.Text.Json.Serialization.JsonPropertyName("cmake")] KitCMakeSettings CMake, string? EnvironmentLockSha256);
