using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Commands;

internal static class ProjectConfigurationCommand
{
    private const string Manifest = "cmwf.config.json";
    private const int MaxVariants = 32;
    private const string Usage = "project config <list|plan|lock|verify|workflow> DIRECTORY [--profile NAME|--matrix] [--overrides FILE] [--plan-sha SHA256] [--variant ID]";
    private static readonly string[] Fields =
        ["configuration", "linkage", "optimization", "debugInformation", "instrumentation", "lto", "testing"];

    private sealed record Selection(string? Profile, bool Matrix, string? Overrides, string? Approval, string? Variant, bool Json);
    private sealed record Layer(VariantSettings Settings, Dictionary<string, string> Origins);
    private sealed record VariantCandidate(string Name, Layer Policy);

    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args is not ["list" or "plan" or "lock" or "verify" or "workflow", string directory, .. string[] options])
        {
            throw new ArgumentException(Usage);
        }

        Selection selection = ParseOptions(options);
        string root = Path.GetFullPath(directory);
        if (args[0] == "list")
        {
            if (options.Length != 0 && options is not ["--json"])
            {
                throw new ArgumentException(Usage);
            }
            using JsonDocument document = JsonDocument.Parse(await ReadAsync(root, Manifest, ct));
            JsonElement project = ReadManifest(document);
            await Console.Out.WriteLineAsync(project.GetProperty("profiles").GetRawText());
            return 0;
        }
        bool locking = args[0] == "lock";
        bool hasApproval = selection.Approval is not null;
        bool variantAllowed = selection.Variant is null || args[0] == "workflow";
        if (locking != hasApproval || !variantAllowed)
        {
            throw new ArgumentException(Usage);
        }

        VariantLock plan = await EvaluateAsync(root, selection, ct);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan, VariantJson.Default.VariantLock);
        string digest = BuildVariantConfiguration.Hash(bytes);
        if (args[0] == "plan")
        {
            await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(bytes));
            await Console.Error.WriteLineAsync("Configuration plan SHA-256: " + digest);
            return 0;
        }

        if (args[0] == "lock")
        {
            if (selection.Approval != digest)
            {
                throw new InvalidDataException("stale-configuration-approval: review the current plan before publishing.");
            }
            await PublishAsync(root, digest, bytes, ct);
        }

        string lockPath = BuildVariantConfiguration.CheckedPath(root, BuildVariantConfiguration.LockPath(digest));
        if (!File.Exists(lockPath) || !(await BuildVariantConfiguration.ReadBoundedAsync(lockPath, ct)).AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidDataException("configuration-lock-unavailable: publish the current plan or restore its verified generation.");
        }

        if (args[0] == "workflow")
        {
            WorkflowDocument workflow = CreateWorkflow(plan, digest, selection.Variant);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(workflow, WorkflowJson.Default.WorkflowDocument));
        }
        else if (selection.Json)
        {
            using MemoryStream output = new();
            using (Utf8JsonWriter writer = new(output))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("configurationLockSha256", digest);
                writer.WriteString("path", BuildVariantConfiguration.LockPath(digest));
                writer.WriteEndObject();
            }
            await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(output.ToArray()));
        }
        else
        {
            await Console.Out.WriteLineAsync("Configuration lock SHA-256: " + digest);
        }

        return 0;
    }

    private static Selection ParseOptions(string[] args)
    {
        string? profile = null;
        string? overrides = null;
        string? approval = null;
        string? variant = null;
        bool matrix = false;
        bool json = false;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (!seen.Add(option))
            {
                throw new ArgumentException(Usage);
            }
            switch (option)
            {
                case "--profile" when i + 1 < args.Length: profile = args[++i]; break;
                case "--overrides" when i + 1 < args.Length: overrides = args[++i]; break;
                case "--plan-sha" when i + 1 < args.Length: approval = args[++i]; break;
                case "--variant" when i + 1 < args.Length: variant = args[++i]; break;
                case "--matrix": matrix = true; break;
                case "--json": json = true; break;
                default: throw new ArgumentException(Usage);
            }
        }
        return matrix && profile is not null ? throw new ArgumentException(Usage) :
            new(profile, matrix, overrides, approval, variant, json);
    }

    private static async Task<VariantLock> EvaluateAsync(string root, Selection selection, CancellationToken ct)
    {
        byte[] sdk = await ProjectSdkResolution.ReadVerifiedAsync(root, ct);
        using JsonDocument sdkDocument = JsonDocument.Parse(sdk);
        string sdkIdentity = BuildVariantConfiguration.Hash(sdk);
        string kind = sdkDocument.RootElement.GetProperty("sdk").GetString()!;
        SdkDescriptor descriptor = ProjectSdkCatalog.Resolve(kind, sdkDocument.RootElement.GetProperty("sdkVersion").GetString());
        VariantEnvironment environment = ReadEnvironment(sdkDocument.RootElement.GetProperty("effective"));
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
        VariantLock plan = new(1, BuildVariantConfiguration.Version, sdkIdentity, kind, inputs, []);
        HashSet<string> ids = new(StringComparer.Ordinal);
        List<EvaluatedVariant> variants = [];
        foreach (VariantCandidate candidate in candidates)
        {
            Layer effective = overrides is null ? candidate.Policy : Overlay(candidate.Policy, overrides.RootElement, "override:" + selection.Overrides);
            try
            {
                BuildVariantConfiguration.Validate(effective.Settings, environment, descriptor);
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
            variants.Add(new(candidate.Name, id, effective.Settings, environment, effective.Origins));
        }
        return plan with { Variants = [.. variants] };
    }

    private static VariantEnvironment ReadEnvironment(JsonElement effective) =>
        effective.TryGetProperty("toolchain", out JsonElement toolchain)
            ? new("linux", "x64", "linux", "x64", "gnu", toolchain.GetProperty("generator").GetString()!,
                toolchain.GetProperty("environmentLockSha256").GetString()!)
            : new("native", "native", "native", "native", "ambient", "Ninja", null);

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

    private static WorkflowDocument CreateWorkflow(VariantLock plan, string digest, string? only)
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
                Toolchain = BuildVariantConfiguration.Toolchain(variant.Environment),
                BuildVariant = new(variant.Id, digest)
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

    private static async Task PublishAsync(string root, string digest, byte[] bytes, CancellationToken ct)
    {
        string finalFile = BuildVariantConfiguration.CheckedPath(root, BuildVariantConfiguration.LockPath(digest));
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
