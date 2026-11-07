using System.Text.Json;
using CMakeWorkflow.Pipelines;

static void Check(bool condition, string description)
{
    if (!condition)
    {
        throw new System.Diagnostics.UnreachableException(description);
    }
}

static void Refused(Action operation, string description)
{
    try
    {
        operation();
    }
    catch (ArgumentException)
    {
        return;
    }

    throw new System.Diagnostics.UnreachableException(description);
}

static void RefusedJson(Action operation, string description)
{
    try
    {
        operation();
    }
    catch (JsonException)
    {
        return;
    }

    throw new System.Diagnostics.UnreachableException(description);
}

static PipelineStage Local(string id, IReadOnlyList<string>? dependencies = null,
    string manifest = "examples/hello/workflow.json") => new()
    {
        Id = id,
        Kind = "workflow",
        DependsOn = dependencies ?? [],
        Workflow = new PipelineWorkflowStage { Manifest = manifest }
    };

static PipelineStage Attached(string id, IReadOnlyList<string>? dependencies = null,
    string sha = "0123456789abcdef0123456789abcdef01234567") => new()
    {
        Id = id,
        Kind = "github.actions",
        DependsOn = dependencies ?? [],
        GitHubActions = new PipelineGitHubActionsStage
        {
            Mode = "attach",
            Repository = "cpp20120/CMakeWorkflow",
            Workflow = "ci.yml",
            RunId = 123456,
            RunAttempt = 1,
            ExpectedHeadSha = sha
        }
    };

static PipelineDocument Document(params PipelineStage[] stages) => new()
{
    SchemaVersion = 1,
    Name = "release",
    Stages = stages
};

PipelineDocument source = Document(
    Attached("ci", ["debug"]),
    Local("debug")
);
PipelinePlan plan = PipelinePlanner.Plan(source);
Check(plan.Stages.Count == 2 &&
      plan.Stages[0].Id == "debug" &&
      plan.Stages[1].Id == "ci" &&
      plan.Stages[1].DependsOn.SequenceEqual(["debug"]),
    "Forward references must produce dependency-first order.");

string serialized = PipelineManifest.ToJson(source);
Check(serialized.Contains("\"githubActions\"", StringComparison.Ordinal) &&
      serialized.Contains("\"schemaVersion\":1", StringComparison.Ordinal),
    "Pipeline JSON must use camelCase.");
PipelineDocument parsed = PipelineManifest.ParseJson(serialized);
Check(PipelinePlanner.Plan(parsed).Stages[1].Id == "ci",
    "Source-generated JSON must roundtrip the planner input.");

// Planner returns detached dependencies, not references into mutable input arrays.
string[] inputDependencies = ["debug"];
PipelinePlan detached = PipelinePlanner.Plan(Document(Local("debug"),
    Attached("ci", inputDependencies)));
inputDependencies[0] = "changed";
Check(detached.Stages[1].DependsOn.Single() == "debug",
    "Plan dependencies must not change after input mutation.");

Refused(() => PipelinePlanner.Plan(Document(Local("x"), Local("x"))), "Duplicate stage accepted.");
Refused(() => PipelinePlanner.Plan(Document(Local("x", ["missing"]))), "Unknown dependency accepted.");
Refused(() => PipelinePlanner.Plan(Document(Local("a", ["b"]), Local("b", ["a"]))),
    "Dependency cycle accepted.");
Refused(() => PipelinePlanner.Plan(Document(Local("x", ["x"]))), "Self-dependency accepted.");
Refused(() => PipelinePlanner.Plan(Document(Local("x", ["x", "x"]))), "Duplicate dependency accepted.");
Refused(() => PipelinePlanner.Plan(Document(Local("x", manifest: "../escape.json"))),
    "Parent traversal accepted.");
Refused(() => PipelinePlanner.Plan(Document(Local("x", manifest: "C:\\escape.json"))),
    "Absolute Windows path accepted.");
Refused(() => PipelinePlanner.Plan(Document(Attached("ci", sha: "bad"))),
    "Unpinned GitHub run accepted.");
Refused(() => PipelinePlanner.Plan(new PipelineDocument
{
    SchemaVersion = 2,
    Name = "release",
    Stages = [Local("x")]
}), "Unknown pipeline schema accepted.");
Refused(() => PipelinePlanner.Plan(Document(new PipelineStage
{
    Id = "ci",
    Kind = "github.actions",
    GitHubActions = new PipelineGitHubActionsStage
    {
        Mode = "dispatch",
        Repository = "cpp20120/CMakeWorkflow",
        Workflow = "ci.yml",
        RunId = 1,
        RunAttempt = 1,
        ExpectedHeadSha = "0123456789abcdef0123456789abcdef01234567"
    }
})), "Unsafe GitHub dispatch accepted.");
Refused(() => PipelinePlanner.Plan(Document(new PipelineStage
{
    Id = "x",
    Kind = "shell"
})), "Unknown stage kind accepted.");
Refused(() => PipelinePlanner.Plan(Document(new PipelineStage
{
    Id = "x",
    Kind = "workflow",
    Workflow = new PipelineWorkflowStage { Manifest = "x.json" },
    GitHubActions = Attached("ci").GitHubActions
})), "Ambiguous stage options accepted.");

RefusedJson(() => PipelineManifest.ParseJson( /*lang=json,strict*/ """
                                                                   {"schemaVersion":1,"name":"release","stages":[],"unknown":true}
                                                                   """), "Unknown JSON fields accepted.");
RefusedJson(() => PipelineManifest.ParseJson( /*lang=json,strict*/ """
                                                                   {"schemaVersion":1,"name":"release","stages":[{"kind":"workflow","workflow":{"manifest":"x.json"}}]}
                                                                   """), "Missing JSON required fields accepted.");

Console.WriteLine("Pipeline v1 manifest, planner, and refusal checks passed.");