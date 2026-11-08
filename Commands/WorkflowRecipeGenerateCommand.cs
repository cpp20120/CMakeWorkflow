using System.Globalization;
using System.Text.Json;
using CMakeWorkflow.CMake.Recipes;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Profiling.Workflows;

namespace CMakeWorkflow.Commands;

internal static class WorkflowRecipeGenerateCommand
{
    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("workflow generate requires a recipe kind.");
        }

        string kind = args[0];
        if (kind is not ("benchmark" or "profile" or "test-matrix" or "build-matrix" or "benchmark-matrix"))
        {
            throw new ArgumentException($"Unsupported workflow recipe kind: {kind}.");
        }

        int delimiter = Array.IndexOf(args, "--");
        if (kind == "benchmark" && !args.AsSpan()[..(delimiter < 0 ? args.Length : delimiter)].Contains("--recipe", StringComparer.Ordinal))
        {
            return await BenchmarkGenerateCommand.ExecuteAsync(args, ct);
        }

        List<(string Name, string Value)> options = [];
        string[] programArguments = [];
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                programArguments = args[(i + 1)..];
                break;
            }

            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length
                                                                    || args[i + 1].StartsWith("--",
                                                                        StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid {kind} option: {args[i]}.");
            }

            options.Add((args[i], args[++i]));
        }

        string? Single(string name)
        {
            string[] values = [.. options.Where(x => x.Name == name).Select(x => x.Value)];
            return values.Length > 1 ? throw new ArgumentException($"Duplicate {kind} option: {name}.") : values.Length == 0 ? null : values[0];
        }

        string Required(string name) => Single(name)
                                        ?? throw new ArgumentException($"{kind} requires {name}.");

        string[] Many(string name) => [.. options.Where(x => x.Name == name).Select(x => x.Value)];
        string output = Path.GetFullPath(Required("--output"));
        string? recipeFile = Single("--recipe");
        WorkflowDocument document;
        if (recipeFile is not null)
        {
            if (options.Any(x => x.Name is not ("--recipe" or "--output")) || programArguments.Length != 0)
            {
                throw new ArgumentException("--recipe accepts only --output; edit recipe fields in the JSON file.");
            }

            string path = Path.GetFullPath(recipeFile);
            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            string baseDirectory = Path.GetDirectoryName(path)!;
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
            string[] allowed = kind switch
            {
                "profile" =>
                [
                    "--build-dir", "--variant", "--target", "--config", "--repeats",
                    "--timeout-seconds", "--backend", "--mode", "--platform", "--source-dir", "--preset", "--output"
                ],
                "test-matrix" =>
                [
                    "--build-dir", "--target", "--config", "--label", "--test",
                    "--order", "--timeout-seconds", "--output"
                ],
                "build-matrix" => ["--variant", "--target", "--config", "--output"],
                _ => ["--output"]
            };
            foreach ((string? Name, string? Value) in options)
            {
                if (!allowed.Contains(Name, StringComparer.Ordinal))
                {
                    throw new ArgumentException($"Unknown {kind} option: {Name}.");
                }
            }

            if (kind != "profile" && programArguments.Length != 0)
            {
                throw new ArgumentException($"{kind} does not accept executable arguments after --.");
            }

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
        BenchmarkRecipeV1 recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.BenchmarkRecipeV1)
                     ?? throw new JsonException("Empty Benchmark recipe.");
        return recipe.BuildDirectory is null
            ? throw new ArgumentException("Benchmark buildDirectory is required.")
            : BenchmarkWorkflowBuilder.Build(recipe with { BuildDirectory = resolve(recipe.BuildDirectory) });
    }

    private static WorkflowDocument FromProfileFile(byte[] bytes, Func<string, string> resolve)
    {
        ProfileRecipeV1 recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.ProfileRecipeV1)
                     ?? throw new JsonException("Empty Profile recipe.");
        return recipe.Variants is null || recipe.Variants.Any(v => v is null || v.BuildDirectory is null)
            ? throw new ArgumentException("Profile variants require buildDirectory.")
            : ProfileRecipeBuilder.Build(recipe with
            {
                Variants = [.. recipe.Variants.Select(v => v with { BuildDirectory = resolve(v.BuildDirectory) })],
                SourceDirectory = recipe.SourceDirectory is null ? null : resolve(recipe.SourceDirectory)
            });
    }

    private static WorkflowDocument FromTestFile(byte[] bytes, Func<string, string> resolve)
    {
        TestMatrixRecipeV1 recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.TestMatrixRecipeV1)
                     ?? throw new JsonException("Empty Test Matrix recipe.");
        return TestMatrixWorkflowBuilder.Build(recipe with
        {
            BuildDirectory = resolve(recipe.BuildDirectory)
        });
    }

    private static WorkflowDocument FromBuildFile(byte[] bytes, Func<string, string> resolve)
    {
        BuildMatrixRecipeV1 recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.BuildMatrixRecipeV1)
                     ?? throw new JsonException("Empty Build Matrix recipe.");
        return recipe.Variants is null || recipe.Variants.Any(v => v is null || v.BuildDirectory is null)
            ? throw new ArgumentException("Build variants require buildDirectory.")
            : BuildMatrixWorkflowBuilder.Build(recipe with
            {
                Variants = [.. recipe.Variants.Select(v =>
                v with { BuildDirectory = resolve(v.BuildDirectory) })]
            });
    }

    private static WorkflowDocument FromBenchmarkMatrixFile(byte[] bytes, Func<string, string> resolve)
    {
        BenchmarkMatrixRecipeV1 recipe = JsonSerializer.Deserialize(bytes, RecipeJson.Default.BenchmarkMatrixRecipeV1)
                     ?? throw new JsonException("Empty Benchmark Matrix recipe.");
        return recipe.Variants is null || recipe.Variants.Any(v => v is null || v.BuildDirectory is null)
            ? throw new ArgumentException("Benchmark variants require buildDirectory.")
            : BenchmarkMatrixWorkflowBuilder.Build(recipe with
            {
                Variants = [.. recipe.Variants.Select(v =>
                v with { BuildDirectory = resolve(v.BuildDirectory) })]
            });
    }

    private static WorkflowDocument DirectProfile(Func<string, string> required,
        Func<string, string?> single, Func<string, string[]> many, string[] arguments)
    {
        string[] variants = many("--variant");
        string? build = single("--build-dir");
        if ((build is null) == (variants.Length == 0))
        {
            throw new ArgumentException("Profile requires either --build-dir or one or more --variant name=DIR.");
        }

        ProfileVariant[] resolved = build is not null
            ? [new ProfileVariant("main", Path.GetFullPath(build))]
            : [.. variants.Select(value =>
            {
                (string? Name, string? Value) = Pair(value, "--variant");
                return new ProfileVariant(Name, Path.GetFullPath(Value));
            })];
        return ProfileRecipeBuilder.Build(new ProfileRecipeV1("profile", 1, resolved,
            required("--target"), single("--config"), Integer(single("--repeats") ?? "3", "--repeats"),
            Number(single("--timeout-seconds") ?? "300", "--timeout-seconds"), arguments,
            single("--backend") ?? "auto", single("--mode") ?? "cpu", required("--platform"),
            single("--source-dir") is { } source ? Path.GetFullPath(source) : null, single("--preset")));
    }

    private static WorkflowDocument DirectTests(Func<string, string> required,
        Func<string, string?> single, List<(string Name, string Value)> options)
    {
        List<TestMatrixGroupV1> groups = [];
        foreach ((string? name, string? value) in options)
        {
            if (name == "--label")
            {
                groups.Add(new(value, LabelRegex: value));
            }

            if (name == "--test")
            {
                (string? Name, string? Value) = Pair(value, "--test");
                groups.Add(new(Name, TestRegex: Value));
            }
        }

        return TestMatrixWorkflowBuilder.Build(new TestMatrixRecipeV1("test-matrix", 1,
            Path.GetFullPath(required("--build-dir")), required("--target"), single("--config"),
            [.. groups], single("--order") ?? "parallel",
            Number(single("--timeout-seconds") ?? "300", "--timeout-seconds")));
    }

    private static WorkflowDocument DirectBuild(Func<string, string?> single,
        Func<string, string[]> many)
    {
        string? configuration = single("--config");
        BuildVariantV1[] variants = [.. many("--variant").Select(value =>
        {
            (string? Name, string? Value) = Pair(value, "--variant");
            return new BuildVariantV1(Name, Path.GetFullPath(Value), configuration);
        })];
        return BuildMatrixWorkflowBuilder.Build(new BuildMatrixRecipeV1("build-matrix", 1,
            variants, many("--target")));
    }

    private static (string Name, string Value) Pair(string value, string option)
    {
        int index = value.IndexOf('=');
        return index <= 0 || index == value.Length - 1
            ? throw new ArgumentException($"{option} requires name=value.")
            : ((string Name, string Value))(value[..index], value[(index + 1)..]);
    }

    private static int Integer(string value, string option) => int.TryParse(value,
        NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
        ? parsed
        : throw new ArgumentException($"{option} must be an integer.");

    private static double Number(string value, string option) => double.TryParse(value,
        NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
        ? parsed
        : throw new ArgumentException($"{option} must be a number.");
}