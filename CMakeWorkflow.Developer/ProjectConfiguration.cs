using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Developer;

internal static class ProjectConfiguration
{
    private const string Manifest = "cmwf.config.json";
    private const int MaxVariants = 32;
    private static readonly string[] Fields =
        ["configuration", "linkage", "optimization", "debugInformation", "instrumentation", "lto", "testing"];

    private sealed record Layer(VariantSettings Settings, Dictionary<string, string> Origins);
    private sealed record VariantCandidate(string Name, Layer Policy);

    internal static async Task<JsonElement> ReadProfilesAsync(string root, CancellationToken ct)
    {
        using JsonDocument document = JsonDocument.Parse(await ReadAsync(root, Manifest, ct));
        return ReadManifest(document).GetProperty("profiles").Clone();
    }

    internal static async Task<VariantLock> EvaluateAsync(string root, ConfigurationSelection selection, CancellationToken ct)
    {
        byte[] sdk = await ProjectSdkResolution.ReadVerifiedAsync(root, ct);
        using JsonDocument sdkDocument = JsonDocument.Parse(sdk);
        string sdkIdentity = BuildVariantConfiguration.Hash(sdk);
        string kind = sdkDocument.RootElement.GetProperty("sdk").GetString()!;
        SdkDescriptor descriptor = ProjectSdkCatalog.Resolve(kind, sdkDocument.RootElement.GetProperty("sdkVersion").GetString());
        VariantEnvironment environment = BuildVariantConfiguration.SdkEnvironment(sdkDocument.RootElement.GetProperty("effective"));
        EngineeringKitPlan? kitPlan = null;
        if (selection.Kit is { } kitId)
        {
            kitPlan = await DeveloperLifecycle.PlanKitCoreAsync(root, kitId, sdkDocument.RootElement, ct);
            if (!kitPlan.Approved)
            {
                throw new InvalidDataException($"kit-lock-unavailable: run kit lock {kitId} --plan-sha {kitPlan.Lock.Sha256}.");
            }
            EngineeringKitEvidence.RequireSupportedDependencies(root);
            environment = EngineeringKitEvidence.Compose(kitPlan.Environment, environment, descriptor);
        }
        SortedDictionary<string, string> inputs = new(StringComparer.Ordinal) { [BuildVariantConfiguration.SdkLock] = sdkIdentity };
        foreach (string collection in new[] { "inputs", "modules" })
        {
            foreach (JsonProperty entry in sdkDocument.RootElement.GetProperty(collection).EnumerateObject())
            {
                inputs.Add(entry.Name, entry.Value.GetString()!);
            }
        }

        byte[] module = await ReadEvidenceAsync(root, BuildVariantConfiguration.Module, inputs, ct);
        byte[] expectedModule = await ResourceAsync("Variant.cmake", ct);
        if (!module.AsSpan().SequenceEqual(expectedModule))
        {
            throw new InvalidDataException("configuration-module-changed: vendored module differs from configuration extension v1.");
        }

        using JsonDocument config = JsonDocument.Parse(await ReadEvidenceAsync(root, Manifest, inputs, ct));
        JsonElement project = ReadManifest(config);
        Layer baseline = new(new VariantSettings(), Fields.ToDictionary(key => key, _ => "extension-default", StringComparer.Ordinal));
        if (project.TryGetProperty("policyFiles", out JsonElement layers))
        {
            if (layers.ValueKind != JsonValueKind.Array || layers.GetArrayLength() > 32)
            {
                throw new InvalidDataException("configuration-layers-limit: expected at most 32 policy files.");
            }
            foreach (JsonElement path in layers.EnumerateArray())
            {
                string name = path.GetString() ?? "";
                using JsonDocument layer = JsonDocument.Parse(await ReadEvidenceAsync(root, name, inputs, ct));
                baseline = Overlay(baseline, layer.RootElement, "policy:" + name);
            }
        }
        if (project.TryGetProperty("settings", out JsonElement settings))
        {
            baseline = Overlay(baseline, settings, "project");
        }

        JsonElement profiles = project.GetProperty("profiles");
        ProjectSdkResolution.CheckObject(profiles, [.. profiles.EnumerateObject().Select(entry => entry.Name)]);
        if (profiles.EnumerateObject().Count() > 64)
        {
            throw new InvalidDataException("configuration-profiles-limit: expected at most 64 profiles.");
        }
        Dictionary<string, Layer> evaluated = new(StringComparer.Ordinal);
        foreach (JsonProperty profile in profiles.EnumerateObject())
        {
            if (!ValidName(profile.Name))
            {
                throw new InvalidDataException("invalid-profile-name: " + profile.Name);
            }
            evaluated.Add(profile.Name, Overlay(baseline, profile.Value, "profile:" + profile.Name));
        }

        VariantCandidate[] candidates;
        if (selection.Matrix)
        {
            candidates = Expand(project.GetProperty("matrix"), baseline, evaluated);
        }
        else
        {
            string name = selection.Profile ?? "release";
            candidates = evaluated.TryGetValue(name, out Layer? selected)
                ? [new(name, selected)] : throw new InvalidDataException("unknown-configuration-profile: " + name);
        }

        using JsonDocument? overrides = selection.Overrides is null ? null :
            JsonDocument.Parse(await ReadEvidenceAsync(root, selection.Overrides, inputs, ct));
        if (kitPlan is not null && !inputs.TryAdd(kitPlan.Lock.Path, kitPlan.Lock.Sha256))
        {
            throw new InvalidDataException("duplicate-configuration-kit-input");
        }
        VariantLock plan = kitPlan is null ? new(1, BuildVariantConfiguration.Version, sdkIdentity, kind, inputs, []) :
            new VariantLock(2, BuildVariantConfiguration.KitVersion, sdkIdentity, kind, inputs, [])
            {
                KitLockSha256 = kitPlan.Lock.Sha256
            };
        HashSet<string> ids = new(StringComparer.Ordinal);
        List<EvaluatedVariant> variants = [];
        foreach (VariantCandidate candidate in candidates)
        {
            Layer effective = overrides is null ? candidate.Policy : Overlay(candidate.Policy, overrides.RootElement, "override:" + selection.Overrides);
            try
            {
                BuildVariantConfiguration.Validate(effective.Settings, environment, descriptor, kitPlan?.Environment);
            }
            catch (InvalidDataException error)
            {
                throw new InvalidDataException(candidate.Name + ": " + error.Message, error);
            }
            string id = BuildVariantConfiguration.Identity(plan, effective.Settings, environment);
            if (!ids.Add(id))
            {
                throw new InvalidDataException("duplicate-variant: " + candidate.Name + " resolves to a previously selected configuration.");
            }
            variants.Add(new(candidate.Name, id, effective.Settings, environment, effective.Origins) { Kit = kitPlan?.Environment });
        }
        return plan with { Variants = [.. variants] };
    }

    private static JsonElement ReadManifest(JsonDocument document)
    {
        JsonElement root = document.RootElement;
        ProjectSdkResolution.CheckObject(root, "schemaVersion", "policyFiles", "profiles", "settings", "matrix");
        return root.GetProperty("schemaVersion").GetInt32() == 1 ? root :
            throw new InvalidDataException("unsupported-configuration-schema");
    }

    private static bool ValidName(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static Layer Overlay(Layer layer, JsonElement values, string origin)
    {
        ProjectSdkResolution.CheckObject(values, Fields);
        Layer current = layer;
        foreach (JsonProperty value in values.EnumerateObject())
        {
            current = Set(current, value.Name, value.Value, origin);
        }
        return current;
    }

    private static Layer Set(Layer layer, string field, JsonElement value, string origin)
    {
        VariantSettings settings = layer.Settings;
        VariantSettings updated = field switch
        {
            "configuration" => settings with { Configuration = EnumValue<VariantConfiguration>(value, field) },
            "linkage" => settings with { Linkage = EnumValue<VariantLinkage>(value, field) },
            "optimization" => settings with { Optimization = EnumValue<VariantOptimization>(value, field) },
            "debugInformation" => settings with { DebugInformation = Switch(value, field) },
            "instrumentation" => settings with { Instrumentation = Instrumentation(value) },
            "lto" => settings with { Lto = Switch(value, field) },
            "testing" when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                settings with { Testing = value.GetBoolean() },
            _ => throw new InvalidDataException("invalid-configuration-field: " + field)
        };
        return new(updated, new Dictionary<string, string>(layer.Origins, StringComparer.Ordinal) { [field] = origin });
    }

    private static T EnumValue<T>(JsonElement value, string field) where T : struct, Enum
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return Enum.TryParse(text, ignoreCase: true, out T result) && Enum.IsDefined(result) &&
               string.Equals(result.ToString(), text, StringComparison.OrdinalIgnoreCase)
            ? result : throw new InvalidDataException("invalid-configuration-value: " + field);
    }

    private static VariantSwitch Switch(JsonElement value, string field) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() ? VariantSwitch.Enabled : VariantSwitch.Disabled
            : EnumValue<VariantSwitch>(value, field);

    private static VariantInstrumentation Instrumentation(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return EnumValue<VariantInstrumentation>(value, "instrumentation");
        }
        HashSet<VariantInstrumentation> entries = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (!entries.Add(EnumValue<VariantInstrumentation>(item, "instrumentation")))
            {
                throw new InvalidDataException("duplicate-instrumentation");
            }
        }
        return entries.Count switch
        {
            0 => VariantInstrumentation.None,
            1 => entries.Single(),
            2 when entries.Contains(VariantInstrumentation.Address) && entries.Contains(VariantInstrumentation.Undefined) =>
                VariantInstrumentation.AddressUndefined,
            _ => throw new InvalidDataException("incompatible-instrumentation: only address and undefined can be combined.")
        };
    }

    private static VariantCandidate[] Expand(JsonElement matrix, Layer baseline, Dictionary<string, Layer> profiles)
    {
        ProjectSdkResolution.CheckObject(matrix, "profile", "axes", "include", "exclude");
        Layer seed = matrix.TryGetProperty("profile", out JsonElement profile)
            ? profiles.GetValueOrDefault(profile.GetString() ?? "") ?? throw new InvalidDataException("unknown-matrix-profile")
            : baseline;
        List<Layer> combinations = [seed];
        JsonElement axes = matrix.GetProperty("axes");
        ProjectSdkResolution.CheckObject(axes, Fields);
        int axisCount = 0;
        foreach (JsonProperty axis in axes.EnumerateObject().OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            axisCount++;
            if (axis.Value.ValueKind != JsonValueKind.Array || axis.Value.GetArrayLength() == 0 ||
                (long)combinations.Count * axis.Value.GetArrayLength() > MaxVariants)
            {
                throw new InvalidDataException("matrix-expansion-limit: axes must be nonempty and produce at most 32 combinations.");
            }
            combinations = [.. combinations.SelectMany(layer => axis.Value.EnumerateArray()
                .Select(value => Set(layer, axis.Name, value, "matrix:" + axis.Name)))];
        }
        if (axisCount == 0 && !matrix.TryGetProperty("include", out _))
        {
            throw new InvalidDataException("empty-configuration-matrix");
        }
        JsonElement[] includes = Rules(matrix, "include");
        if (axisCount == 0)
        {
            combinations.Clear();
        }
        if (combinations.Count + includes.Length > MaxVariants)
        {
            throw new InvalidDataException("matrix-expansion-limit: include rules exceed the 32-variant limit.");
        }
        combinations.AddRange(includes.Select(rule => Overlay(seed, rule, "matrix:include")));
        foreach (JsonElement rule in Rules(matrix, "exclude"))
        {
            _ = Overlay(seed, rule, "matrix:exclude");
            if (!rule.EnumerateObject().Any())
            {
                throw new InvalidDataException("empty-matrix-exclude");
            }
            _ = combinations.RemoveAll(layer => Overlay(layer, rule, "matrix:exclude").Settings == layer.Settings);
        }
        return combinations.Count == 0 ? throw new InvalidDataException("empty-configuration-matrix") :
            [.. combinations.Select((layer, index) => new VariantCandidate(
                "variant-" + (index + 1).ToString("D2", CultureInfo.InvariantCulture), layer))];
    }

    private static JsonElement[] Rules(JsonElement matrix, string name) =>
        !matrix.TryGetProperty(name, out JsonElement rules) ? [] :
        rules.ValueKind == JsonValueKind.Array && rules.GetArrayLength() <= MaxVariants
            ? [.. rules.EnumerateArray()] : throw new InvalidDataException("matrix-rule-limit: " + name);

    internal static WorkflowDocument CreateWorkflow(VariantLock plan, string digest, string? only)
    {
        WorkflowDocument document = new() { SchemaVersion = 2 };
        foreach (EvaluatedVariant variant in plan.Variants.Where(item => only is null || item.Id == only || item.Name == only))
        {
            string context = variant.Name;
            document.Contexts.Add(context, new WorkflowContext
            {
                SourceDirectory = ".",
                BuildDirectory = BuildVariantConfiguration.BuildDirectory(variant),
                Mode = "managed",
                Configuration = variant.Effective.Configuration.ToString(),
                Generator = variant.Environment.Generator,
                Toolchain = BuildVariantConfiguration.Toolchain(variant),
                BuildVariant = new(variant.Id, digest) { KitLockSha256 = plan.KitLockSha256 }
            });
            document.Nodes.Add(new() { Id = context + "-configure", Kind = "configure", Context = context });
            document.Nodes.Add(new() { Id = context + "-build", Kind = "invoke", Context = context, Target = "all", DependsOn = [context + "-configure"] });
            if (variant.Effective.Testing)
            {
                document.Nodes.Add(new() { Id = context + "-test", Kind = "ctest", Context = context, DependsOn = [context + "-build"] });
            }
        }
        return document.Contexts.Count > 0 ? document : throw new InvalidDataException("unknown-workflow-variant");
    }

    internal static async Task InitializeAsync(string root, CancellationToken ct)
    {
        string module = Path.Combine(root, BuildVariantConfiguration.Module);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(module)!);
        await File.WriteAllBytesAsync(module, await ResourceAsync("Variant.cmake", ct), ct);
        string configuration = Path.Combine(root, Manifest);
        if (!File.Exists(configuration))
        {
            await File.WriteAllBytesAsync(configuration, await ResourceAsync("config.json", ct), ct);
        }
        await File.AppendAllTextAsync(Path.Combine(root, ".gitattributes"), "cmake/cmwf-config/v1/* text eol=lf\n", ct);
    }

    internal static async Task<byte[]> ResourceAsync(string name, CancellationToken ct)
    {
        using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("CMakeWorkflow.ProjectSdk.config-v1." + name)
            ?? throw new InvalidOperationException("Missing configuration extension resource: " + name);
        using StreamReader reader = new(input);
        return Encoding.UTF8.GetBytes((await reader.ReadToEndAsync(ct)).ReplaceLineEndings("\n"));
    }

    private static Task<byte[]> ReadAsync(string root, string path, CancellationToken ct) =>
        BuildVariantConfiguration.ReadBoundedAsync(BuildVariantConfiguration.CheckedPath(root, path), ct);

    private static async Task<byte[]> ReadEvidenceAsync(string root, string path, SortedDictionary<string, string> inputs, CancellationToken ct)
    {
        if (inputs.Keys.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("duplicate-configuration-input: " + path);
        }
        byte[] bytes = await ReadAsync(root, path, ct);
        inputs.Add(path, BuildVariantConfiguration.Hash(bytes));
        return bytes;
    }

    internal static Task PublishAsync(string root, string digest, byte[] bytes, CancellationToken ct) =>
        PublishEvidenceAsync(root, BuildVariantConfiguration.LockPath(digest), bytes, ct);

    internal static async Task PublishEvidenceAsync(string root, string relative, byte[] bytes, CancellationToken ct)
    {
        string finalFile = BuildVariantConfiguration.CheckedPath(root, relative);
        string store = Path.GetDirectoryName(Path.GetDirectoryName(finalFile))!;
        _ = Directory.CreateDirectory(store);
        string staging = Path.Combine(store, ".staging-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(staging, "lock.json"), bytes, ct);
            ct.ThrowIfCancellationRequested();
            string finalDirectory = Path.GetDirectoryName(finalFile)!;
            try
            {
                Directory.Move(staging, finalDirectory);
            }
            catch (IOException) when (Directory.Exists(finalDirectory))
            {
                // The caller validates the existing bytes before returning; preserve any other contents.
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }
}
