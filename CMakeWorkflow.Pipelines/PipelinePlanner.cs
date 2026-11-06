namespace CMakeWorkflow.Pipelines;

/// <summary>A detached, immutable presentation of pipeline dependency order.</summary>
/// <remarks>Planning proves only graph and static option validity, not availability or authorization.</remarks>
public sealed record PipelinePlan(string Name, IReadOnlyList<PipelinePlanStage> Stages);

/// <summary>One planned stage with dependency IDs detached from the input document.</summary>
public sealed record PipelinePlanStage(string Id, string Kind, IReadOnlyList<string> DependsOn);

/// <summary>Validates the entire pipeline graph and produces stable dependency-first ordering.</summary>
/// <remarks>No filesystem, network, credentials, workflow dispatch, or child process execution occurs here.</remarks>
public static class PipelinePlanner
{
    private const int MaxStages = 256;

    public static PipelinePlan Plan(PipelineDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1)
        {
            throw new ArgumentException("Unsupported pipeline schemaVersion; expected 1.", nameof(document));
        }

        Identifier(document.Name, "pipeline name");
        IReadOnlyList<PipelineStage> stages = document.Stages
                                              ?? throw new ArgumentException("Pipeline stages cannot be null.",
                                                  nameof(document));
        if (stages.Count is < 1 or > MaxStages)
        {
            throw new ArgumentException("Pipeline requires 1..256 stages.", nameof(document));
        }

        Dictionary<string, int> indices = new(StringComparer.Ordinal);
        for (int i = 0; i < stages.Count; i++)
        {
            PipelineStage stage = stages[i]
                                  ?? throw new ArgumentException("Pipeline stage cannot be null.", nameof(document));
            Identifier(stage.Id, "stage ID");
            if (!indices.TryAdd(stage.Id, i))
            {
                throw new ArgumentException($"Duplicate stage ID '{stage.Id}'.", nameof(document));
            }

            ValidateOptions(stage);
        }

        foreach (PipelineStage stage in stages)
        {
            if (stage.DependsOn is null)
            {
                throw new ArgumentException($"Dependencies cannot be null for stage '{stage.Id}'.", nameof(document));
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string dependency in stage.DependsOn)
            {
                if (dependency is null || !indices.ContainsKey(dependency))
                {
                    throw new ArgumentException($"Unknown prerequisite '{dependency}' for stage '{stage.Id}'.",
                        nameof(document));
                }

                if (!seen.Add(dependency))
                {
                    throw new ArgumentException($"Duplicate prerequisite '{dependency}' for stage '{stage.Id}'.",
                        nameof(document));
                }
            }
        }

        byte[] state = new byte[stages.Count];
        List<PipelinePlanStage> ordered = new(stages.Count);
        for (int i = 0; i < stages.Count; i++)
        {
            Visit(i);
        }

        return new PipelinePlan(document.Name, Array.AsReadOnly(ordered.ToArray()));

        void Visit(int index)
        {
            if (state[index] == 2)
            {
                return;
            }

            if (state[index] == 1)
            {
                throw new ArgumentException($"Cycle detected through stage '{stages[index].Id}'.", nameof(document));
            }

            state[index] = 1;
            PipelineStage stage = stages[index];
            foreach (string dependency in stage.DependsOn)
            {
                Visit(indices[dependency]);
            }

            state[index] = 2;
            ordered.Add(new PipelinePlanStage(stage.Id, stage.Kind,
                Array.AsReadOnly(stage.DependsOn.ToArray())));
        }
    }

    private static void ValidateOptions(PipelineStage stage)
    {
        switch (stage.Kind)
        {
            case "workflow":
                if (stage.Workflow is null || stage.GitHubActions is not null)
                {
                    throw new ArgumentException($"Stage '{stage.Id}' requires only workflow options.");
                }

                RelativeManifest(stage.Workflow.Manifest);
                break;

            case "github.actions":
                if (stage.GitHubActions is null || stage.Workflow is not null)
                {
                    throw new ArgumentException($"Stage '{stage.Id}' requires only githubActions options.");
                }

                ValidateGitHub(stage.GitHubActions);
                break;

            default:
                throw new ArgumentException($"Unsupported stage kind '{stage.Kind}'.");
        }
    }

    private static void ValidateGitHub(PipelineGitHubActionsStage options)
    {
        if (options.Mode is not ("attach" or "dispatch"))
        {
            throw new ArgumentException("GitHub Actions supports attach and dispatch only.");
        }

        string[] parts = (options.Repository ?? "").Split('/');
        if (parts.Length != 2 || !ValidIdentifier(parts[0]) || !ValidIdentifier(parts[1]))
        {
            throw new ArgumentException("GitHub repository must be owner/name.");
        }

        RelativeManifest(options.Workflow);
        if (!options.Workflow.EndsWith(".yml", StringComparison.Ordinal) &&
            !options.Workflow.EndsWith(".yaml", StringComparison.Ordinal))
        {
            throw new ArgumentException("GitHub workflow must name a YAML workflow file.");
        }

        if (options.Mode == "attach")
        {
            if (options.RunId <= 0 || options.RunAttempt <= 0 || options.Ref is not null || options.Inputs is not null)
            {
                throw new ArgumentException("GitHub attach requires positive runId/attempt and no dispatch options.");
            }
        }
        else
        {
            string workflowFile = options.Workflow.StartsWith(".github/workflows/", StringComparison.Ordinal)
                ? options.Workflow[".github/workflows/".Length..] : options.Workflow;
            if (workflowFile.Contains('/', StringComparison.Ordinal))
            {
                throw new ArgumentException("GitHub dispatch requires a workflow YAML filename.");
            }

            string? branchRef = options.Ref;
            if (options.RunId != 0 || options.RunAttempt != 0 ||
                string.IsNullOrWhiteSpace(branchRef) || branchRef.Length > 255 ||
                !(branchRef.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                  branchRef.StartsWith("refs/tags/", StringComparison.Ordinal)) ||
                branchRef.Any(char.IsControl) || branchRef.Any(char.IsWhiteSpace) ||
                branchRef.Split('/').Any(static part => part is "" or "." or "..") ||
                options.Inputs is { Count: > 24 })
            {
                throw new ArgumentException("GitHub dispatch requires a bounded branch/tag ref, no run ID, and <=24 inputs.");
            }

            int bytes = 0;
            foreach (KeyValuePair<string, string> input in options.Inputs ??
                         new Dictionary<string, string>())
            {
                if (input.Key == "cmwf_request_id" || input.Key.Length is < 1 or > 64 ||
                    !input.Key.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-') ||
                    input.Value is null || input.Value.Length > 65535)
                {
                    throw new ArgumentException("Invalid or reserved GitHub dispatch input.");
                }

                bytes += System.Text.Encoding.UTF8.GetByteCount(input.Key) +
                    System.Text.Encoding.UTF8.GetByteCount(input.Value);
                if (bytes > 64000)
                {
                    throw new ArgumentException("GitHub dispatch input payload too large.");
                }
            }
        }

        string sha = options.ExpectedHeadSha ?? "";
        if (sha.Length != 40 || !sha.All(static c => char.IsAsciiHexDigit(c)))
        {
            throw new ArgumentException("GitHub expectedHeadSha must be a 40-character Git commit SHA.");
        }
    }

    private static void RelativeManifest(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 ||
            path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            throw new ArgumentException("Manifest/workflow file must be a workspace-relative path.");
        }

        string[] segments = path.Replace('\\', '/').Split('/');
        if (segments.Any(static part => part is "" or "." or ".."))
        {
            throw new ArgumentException("Manifest/workflow file cannot contain empty, dot or parent segments.");
        }
    }

    private static void Identifier(string value, string field)
    {
        if (!ValidIdentifier(value))
        {
            throw new ArgumentException($"Invalid {field} '{value}'.");
        }
    }

    private static bool ValidIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 128 && value is not ("." or "..") &&
        value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
}