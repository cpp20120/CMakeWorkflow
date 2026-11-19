using System.Text.Json;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Recipes;

internal static class BenchmarkMetricExtractor
{
    private const int MaxStdoutBytes = 4 * 1024 * 1024;

    internal static void Validate(BenchmarkMetrics? metrics)
    {
        if (metrics is null)
        {
            return;
        }

        if (metrics.Source != "stdout" || metrics.Format != "json" || metrics.Fields is not { Count: >= 1 and <= 32 })
        {
            throw new ArgumentException("metrics requires source=stdout, format=json and 1..32 fields.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (BenchmarkMetricField? field in metrics.Fields)
        {
            if (field is null || !IsPath(field.Path)
                || !IsIdentifier(field.Name) || field.Name is "process_wall_time" or "peak_rss_bytes" or "cpu_time" or "artifact_result_bytes"
                || !names.Add(field.Name)
                || field.Unit is not { Length: >= 1 and <= 32 } || field.Unit.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
                || field.Direction is not ("lower-is-better" or "higher-is-better"))
            {
                throw new ArgumentException("metrics.fields requires a dotted property path, a unique non-reserved name, a unit and direction lower-is-better or higher-is-better.");
            }
        }
    }

    private static bool IsPath(string? value) => value is { Length: >= 1 and <= 256 }
        && value.Split('.').All(IsIdentifier);

    private static bool IsIdentifier(string? value) => value is { Length: >= 1 and <= 64 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal static async Task<IReadOnlyList<BenchmarkMetricSample>> ReadAsync(
        BenchmarkMetrics metrics, string stdout, CancellationToken ct)
    {
        await using FileStream stream = new(stdout, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxStdoutBytes)
        {
            throw new InvalidDataException("Benchmark metrics require JSON stdout of at most 4 MiB.");
        }

        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(stream,
                new JsonDocumentOptions { AllowDuplicateProperties = false }, ct);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? metrics.Fields.Select(field => new BenchmarkMetricSample(field.Name, field.Unit,
                    field.Direction, field.Path, ReadValue(document.RootElement, field.Path))).ToArray()
                : throw new InvalidDataException("Benchmark metrics require one JSON object on stdout.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Benchmark metrics require one valid JSON object on stdout.", error);
        }
    }

    private static double ReadValue(JsonElement element, string path)
    {
        foreach (string segment in path.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                throw new InvalidDataException($"Benchmark metric path '{path}' is missing from stdout.");
            }
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double value) && double.IsFinite(value)
            ? value
            : throw new InvalidDataException($"Benchmark metric path '{path}' must contain a finite JSON number.");
    }
}
