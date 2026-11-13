namespace CMakeWorkflow.Host.Workflows;

internal static class WorkflowNodeSelection
{
    internal static string[] Normalize(IReadOnlyList<string>? only) =>
        only is null ? []
        : only.Count > 1000 || only.Any(string.IsNullOrWhiteSpace)
            ? throw new ArgumentException("only must contain at most 1000 nonempty node IDs.")
            : [.. only.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
