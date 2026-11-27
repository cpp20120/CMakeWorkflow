using System.Globalization;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Artifacts;

// Only complete, explicit argument references are expanded. Arbitrary arguments,
// environment values and paths embedded in scripts keep their original semantics.
internal sealed record SourceBoundManifestArtifacts(string SourceSnapshotId, string InputPrefix,
    string SourceRoot, string OutputRoot, IReadOnlyDictionary<string, string> InputDigests, Action<string> RequireUnredirected)
{
    internal string Input(string path) => Resolve(SourceRoot, path[InputPrefix.Length..]);

    internal string Output(WorkflowArtifactOutput output) => Resolve(OutputRoot, output.Path);

    internal static void ValidateArguments(WorkflowNode node)
    {
        foreach (string argument in node.Arguments)
        {
            if (argument.StartsWith("@input:", StringComparison.Ordinal))
            {
                _ = InputIndex(node, argument);
            }
            else if (argument.StartsWith("@output:", StringComparison.Ordinal))
            {
                _ = OutputDefinition(node, argument);
            }
        }
    }

    internal List<string> Arguments(WorkflowNode node, IReadOnlyList<WorkflowArtifactEvidence> evidence, string run) =>
        [.. node.Arguments.Select(argument => argument switch
        {
            _ when argument.StartsWith("@input:", StringComparison.Ordinal) =>
                evidence[InputIndex(node, argument)].Path,
            _ when argument.StartsWith("@output:", StringComparison.Ordinal) => ResolveOutput(OutputDefinition(node, argument), run),
            _ => argument
        })];

    internal string ResolveOutput(WorkflowArtifactOutput output, string run)
    {
        string path = output.Root == "manifest" ? Output(output) : Path.GetFullPath(Path.Combine(run, output.Path));
        RequireUnredirected(path);
        return path;
    }

    private string Resolve(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        RequireUnredirected(path);
        return path;
    }

    private static int InputIndex(WorkflowNode node, string argument) =>
        int.TryParse(argument.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
        index < node.Inputs.Count
            ? index : throw new InvalidDataException($"Invalid declared input argument in node '{node.Id}'.");

    private static WorkflowArtifactOutput OutputDefinition(WorkflowNode node, string argument) =>
        node.Outputs.SingleOrDefault(output => output.Name == argument[8..])
            ?? throw new InvalidDataException($"Unknown declared output argument in node '{node.Id}'.");
}
