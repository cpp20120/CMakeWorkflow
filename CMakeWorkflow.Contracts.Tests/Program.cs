using System.Text.Json;
using CMakeWorkflow.Contracts.Builders;
using CMakeWorkflow.Contracts.Workflows;

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

WorkflowBuilder workflow = WorkflowBuilder.Create()
    .Context("release", c => c.BuildDirectory("/tmp/cmwf-test/release")
        .Configuration("Release").AdoptExisting().AllowUnverified());
BuildNodeRef build = workflow.BuildTarget("compile", "release", "dagflow_bench",
    n => n.Output("binary", "bin/dagflow_bench"));
WorkflowNodeRef run = workflow.Run("measure", build, n => n.Arguments("--case", "chain")
    .Timeout(TimeSpan.FromSeconds(12)).Resource("measurement-host")
    .InputFrom(build, "binary").RequireCpuSet("2"));
workflow.CTest("unit", "release", build, n => n.LabelRegex("unit").DependsOn(run));
WorkflowDocument snapshot = workflow.ToDocument();
Check(snapshot.SchemaVersion == 2 && snapshot.Contexts["release"].Mode == "adopt"
                                  && snapshot.Contexts["release"].ProvenancePolicy == "allowUnverified",
    "Context construction");
Check(snapshot.Nodes.Count == 3 && snapshot.Nodes[1].Kind == "run"
                                && snapshot.Nodes[1].From == "compile" && snapshot.Nodes[1].Context is null
                                && snapshot.Nodes[1].Arguments.SequenceEqual(["--case", "chain"])
                                && snapshot.Nodes[1].TimeoutSeconds == 12
                                && snapshot.Nodes[1].Inputs.Single().Artifact == "binary"
                                && snapshot.Nodes[1].Constraints?.CpuSet == "2", "Typed build-to-run contract");
Check(snapshot.Nodes[2].DependsOn.SequenceEqual(["compile", "measure"]), "CTest prerequisites");

// Documents returned to callers must not mutate future builder state.
snapshot.Nodes[1].Arguments.Add("--mutated");
snapshot.Nodes[1].Inputs[0].Artifact = "changed";
snapshot.Nodes[1].Constraints!.CpuSet = "3";
snapshot.Contexts["release"].BuildDirectory = "other";
Check(workflow.ToDocument().Nodes[1].Arguments.Count == 2
      && workflow.ToDocument().Nodes[1].Inputs[0].Artifact == "binary"
      && workflow.ToDocument().Nodes[1].Constraints?.CpuSet == "2"
      && workflow.ToDocument().Contexts["release"].BuildDirectory == "/tmp/cmwf-test/release",
    "Document snapshots share mutable state");

// Uses the same camelCase JSON shape as the engine's workflow manifest source generator.
string json = JsonSerializer.Serialize(workflow.ToDocument(), JsonSerializerOptions.Web);
using JsonDocument parsed = JsonDocument.Parse(json);
Check(parsed.RootElement.GetProperty("schemaVersion").GetInt32() == 2
      && parsed.RootElement.GetProperty("nodes")[1].GetProperty("from").GetString() == "compile"
      && parsed.RootElement.GetProperty("contexts").GetProperty("release")
          .GetProperty("provenancePolicy").GetString() == "allowUnverified",
    "Workflow manifest JSON contract");

Refused(() => workflow.BuildTarget("compile", "release", "dagflow_bench"), "Duplicate nodes accepted");
Refused(() => workflow.Run("bad", new BuildNodeRef("nonexistent")), "Invalid from accepted");
Refused(() => workflow.Invoke("missing-context", "unknown", "target"), "Unknown context accepted");
Refused(() => workflow.Context("release", c => c.BuildDirectory("x")), "Duplicate contexts accepted");
Refused(() => workflow.Configure("contains spaces", "release"), "Invalid identifiers accepted");
Refused(() => workflow.Run("bad-dependency", build, n => n.DependsOn(new WorkflowNodeRef("missing"))),
    "Unknown dependencies accepted");
Check(workflow.ToDocument().Nodes.Count == 3, "Rejected operations changed the graph");

Console.WriteLine("Workflow contracts and fluent builder checks passed.");
