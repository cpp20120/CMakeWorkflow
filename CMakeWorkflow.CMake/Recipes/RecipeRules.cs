using System.Text.RegularExpressions;

namespace CMakeWorkflow;

internal static class RecipeRules
{
    internal static void Version(string kind, int version, string expected)
    {
        if (kind != expected || version != 1)
            throw new ArgumentException($"{expected} recipe requires kind={expected} and schemaVersion=1.");
    }

    internal static void Name(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value,
                @"^[A-Za-z0-9_.+\-]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException($"{field} must be a nonempty CMake identifier.");
    }

    internal static void Id(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value,
                @"^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant))
            throw new ArgumentException(
                $"{field} must start with a lowercase letter and contain only lowercase letters, digits or underscores.");
    }

    internal static void Directory(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
                                             || value.Contains('\0'))
            throw new ArgumentException($"{field} must be an absolute path.");
    }

    internal static void Configuration(string? value)
    {
        if (value is not null) Name(value, "configuration");
    }

    internal static void Timeout(double value)
    {
        if (!double.IsFinite(value) || value is <= 0 or > 86400)
            throw new ArgumentException("timeoutSeconds must be positive and at most one day.");
    }

    internal static void Arguments(string[]? args, string field)
    {
        if (args is null || args.Any(a => a is null || a.Contains('\0')))
            throw new ArgumentException($"{field} must be an array of strings without NUL.");
    }

    internal static void DistinctDirectories(IEnumerable<string> directories, string field)
    {
        var paths = directories.Select(Path.GetFullPath).ToArray();
        if (paths.Distinct(WorkflowPlanner.PathComparer).Count() != paths.Length)
            throw new ArgumentException($"{field} must use distinct build directories.");
    }

    internal static WorkflowContext Adopted(string directory, string? configuration) =>
        new()
        {
            BuildDirectory = directory, Configuration = configuration,
            Mode = "adopt", ProvenancePolicy = "allowUnverified"
        };
}