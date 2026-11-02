using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow;

internal static class WorkflowRecipeGenerateCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0) throw new ArgumentException("workflow generate requires a recipe kind.");
        var kind = args[0];
        if (kind is not ("benchmark" or "profile" or "test-matrix" or "build-matrix" or "benchmark-matrix"))
            throw new ArgumentException($"Unsupported workflow recipe kind: {kind}.");
        var delimiter = Array.IndexOf(args, "--");
        if (kind == "benchmark" && !args[..(delimiter < 0 ? args.Length : delimiter)]
            .Contains("--recipe", StringComparer.Ordinal))
            return await BenchmarkGenerateCommand.ExecuteAsync(args, ct);

        var options = new List<(string Name, string Value)>();
        string[] programArguments = [];
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                programArguments = args[(i + 1)..];
                break;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length
                || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid {kind} option: {args[i]}.");
            options.Add((args[i], args[++i]));
        }
        string? Single(string name)
        {
            var values = options.Where(x => x.Name == name).Select(x => x.Value).ToArray();
            if (values.Length > 1) throw new ArgumentException($"Duplicate {kind} option: {name}.");
            return values.Length == 0 ? null : values[0];
        }
        string Required(string name) => Single(name)
            ?? throw new ArgumentException($"{kind} requires {name}.");
        string[] Many(string name) => options.Where(x => x.Name == name).Select(x => x.Value).ToArray();
        var output = Path.GetFullPath(Required("--output"));
        var recipeFile = Single("--recipe");
        WorkflowDocument document;
        if (recipeFile is not null)
        {
            if (options.Any(x => x.Name is not ("--recipe" or "--output")) || programArguments.Length != 0)
                throw new ArgumentException("--recipe accepts only --output; edit recipe fields in the JSON file.");
            var path = Path.GetFullPath(recipeFile);
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var baseDirectory = Path.GetDirectoryName(path)!;
            string Resolve(string value) => Path.GetFullPath(value, baseDirectory);
            document = kind switch
            {
                "benchmark" => FromBenchmarkFile(bytes, Resolve),
                "profile" => FromProfileFile(bytes, Resolve),
                "test-matrix" => FromTestFile(bytes, Resolve),
                "build-matrix" => FromBuildFile(bytes, Resolve),
                _ => FromBenchmarkMatrixFile(bytes, Resolve)
            };
        }
        else
        {
            var allowed = kind switch
            {
                "profile" => new[] { "--build-dir", "--variant", "--target", "--config", "--repeats",
                    "--timeout-seconds", "--backend", "--mode", "--platform", "--source-dir", "--preset", "--output" },
                "test-matrix" => ["--build-dir", "--target", "--config", "--label", "--test",
                    "--order", "--timeout-seconds", "--output"],
                "build-matrix" => ["--variant", "--target", "--config", "--output"],
                _ => ["--output"]
            };
            foreach (var option in options)
                if (!allowed.Contains(option.Name, StringComparer.Ordinal))
                    throw new ArgumentException($"Unknown {kind} option: {option.Name}.");
            if (kind != "profile" && programArguments.Length != 0)
                throw new ArgumentException($"{kind} does not accept executable arguments after --.");
            document = kind switch
            {
                "profile" => DirectProfile(Required, Single, Many, programArguments),
                "test-matrix" => DirectTests(Required, Single, options),
                "build-matrix" => DirectBuild(Single, Many),
                _ => throw new ArgumentException("benchmark-matrix requires --recipe JSON input.")
            };
        }
        await WorkflowManifestBytes.WriteNewAsync(output, document, ct);
        Console.WriteLine(output);
        return 0;
    }

    private static WorkflowDocument FromBenchmarkFile(byte[] bytes, Func<string, string> resolve)
    {
        var recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.BenchmarkRecipeV1)
            ?? throw new JsonException("Empty Benchmark recipe.");
        if (recipe.BuildDirectory is null)
            throw new ArgumentException("Benchmark buildDirectory is required.");
        return BenchmarkWorkflowBuilder.Build(recipe with { BuildDirectory = resolve(recipe.BuildDirectory) });
    }

    private static WorkflowDocument FromProfileFile(byte[] bytes, Func<string, string> resolve)
    {
        var recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.ProfileRecipeV1)
            ?? throw new JsonException("Empty Profile recipe.");
        if (recipe.Variants is null || recipe.Variants.Any(v => v is null || v.BuildDirectory is null))
            throw new ArgumentException("Profile variants require buildDirectory.");
        return ProfileRecipeBuilder.Build(recipe with
        {
            Variants = recipe.Variants.Select(v => v with { BuildDirectory = resolve(v.BuildDirectory) }).ToArray(),
            SourceDirectory = recipe.SourceDirectory is null ? null : resolve(recipe.SourceDirectory)
        });
    }

    private static WorkflowDocument FromTestFile(byte[] bytes, Func<string, string> resolve)
    {
        var recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.TestMatrixRecipeV1)
            ?? throw new JsonException("Empty Test Matrix recipe.");
        return TestMatrixWorkflowBuilder.Build(recipe with
        { BuildDirectory = resolve(recipe.BuildDirectory) });
    }

    private static WorkflowDocument FromBuildFile(byte[] bytes, Func<string, string> resolve)
    {
        var recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.BuildMatrixRecipeV1)
            ?? throw new JsonException("Empty Build Matrix recipe.");
        if (recipe.Variants is null || recipe.Variants.Any(v => v is null || v.BuildDirectory is null))
            throw new ArgumentException("Build variants require buildDirectory.");
        return BuildMatrixWorkflowBuilder.Build(recipe with { Variants = recipe.Variants.Select(v =>
            v with { BuildDirectory = resolve(v.BuildDirectory) }).ToArray() });
    }

    private static WorkflowDocument FromBenchmarkMatrixFile(byte[] bytes, Func<string, string> resolve)
    {
        var recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.BenchmarkMatrixRecipeV1)
            ?? throw new JsonException("Empty Benchmark Matrix recipe.");
        if (recipe.Variants is null || recipe.Variants.Any(v => v is null || v.BuildDirectory is null))
            throw new ArgumentException("Benchmark variants require buildDirectory.");
        return BenchmarkMatrixWorkflowBuilder.Build(recipe with { Variants = recipe.Variants.Select(v =>
            v with { BuildDirectory = resolve(v.BuildDirectory) }).ToArray() });
    }

    private static WorkflowDocument DirectProfile(Func<string, string> required,
        Func<string, string?> single, Func<string, string[]> many, string[] arguments)
    {
        var variants = many("--variant");
        var build = single("--build-dir");
        if ((build is null) == (variants.Length == 0))
            throw new ArgumentException("Profile requires either --build-dir or one or more --variant name=DIR.");
        var resolved = build is not null
            ? new[] { new ProfileVariant("main", Path.GetFullPath(build)) }
            : variants.Select(value =>
            {
                var pair = Pair(value, "--variant");
                return new ProfileVariant(pair.Name, Path.GetFullPath(pair.Value));
            }).ToArray();
        return ProfileRecipeBuilder.Build(new ProfileRecipeV1("profile", 1, resolved,
            required("--target"), single("--config"), Integer(single("--repeats") ?? "3", "--repeats"),
            Number(single("--timeout-seconds") ?? "300", "--timeout-seconds"), arguments,
            single("--backend") ?? "auto", single("--mode") ?? "cpu", required("--platform"),
            single("--source-dir") is { } source ? Path.GetFullPath(source) : null, single("--preset")));
    }

    private static WorkflowDocument DirectTests(Func<string, string> required,
        Func<string, string?> single, List<(string Name, string Value)> options)
    {
        var groups = new List<TestMatrixGroupV1>();
        foreach (var (name, value) in options)
        {
            if (name == "--label") groups.Add(new(value, LabelRegex: value));
            if (name == "--test")
            {
                var pair = Pair(value, "--test");
                groups.Add(new(pair.Name, TestRegex: pair.Value));
            }
        }
        return TestMatrixWorkflowBuilder.Build(new TestMatrixRecipeV1("test-matrix", 1,
            Path.GetFullPath(required("--build-dir")), required("--target"), single("--config"),
            groups.ToArray(), single("--order") ?? "parallel",
            Number(single("--timeout-seconds") ?? "300", "--timeout-seconds")));
    }

    private static WorkflowDocument DirectBuild(Func<string, string?> single,
        Func<string, string[]> many)
    {
        var configuration = single("--config");
        var variants = many("--variant").Select(value =>
        {
            var pair = Pair(value, "--variant");
            return new BuildVariantV1(pair.Name, Path.GetFullPath(pair.Value), configuration);
        }).ToArray();
        return BuildMatrixWorkflowBuilder.Build(new BuildMatrixRecipeV1("build-matrix", 1,
            variants, many("--target")));
    }

    private static (string Name, string Value) Pair(string value, string option)
    {
        var index = value.IndexOf('=');
        if (index <= 0 || index == value.Length - 1)
            throw new ArgumentException($"{option} requires name=value.");
        return (value[..index], value[(index + 1)..]);
    }

    private static int Integer(string value, string option) => int.TryParse(value,
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : throw new ArgumentException($"{option} must be an integer.");

    private static double Number(string value, string option) => double.TryParse(value,
        NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : throw new ArgumentException($"{option} must be a number.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(BenchmarkRecipeV1))]
[JsonSerializable(typeof(ProfileRecipeV1))]
[JsonSerializable(typeof(TestMatrixRecipeV1))]
[JsonSerializable(typeof(BuildMatrixRecipeV1))]
[JsonSerializable(typeof(BenchmarkMatrixRecipeV1))]
internal partial class RecipeJson : JsonSerializerContext;
