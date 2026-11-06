# Pipelines v1 — planning foundation (experimental)

The independent `CMakeWorkflow.Pipelines` assembly introduces a **pure, read-only planner**, not an execution feature.
It does not extend the stable
`CMakeWorkflow.Client` / `CMakeWorkflow.Contracts` SDK package boundary. There is **no pipeline CLI, Host RPC, GitHub
API call, persistent pipeline coordinator, or deployment adapter** in this patch.

Its initial goal is to validate the cross-domain graph before implementing durable orchestration. The two accepted stage
kinds are:

- `workflow`: references an existing local CMakeWorkflow manifest; this patch only checks its workspace-relative lexical
  path.
- `github.actions`: `mode: "attach"` references one **already existing**
  GitHub Actions run and attempt. No dispatch, retry, cancel or API lookup.

Example (the GitHub identity values are illustrative, not a runnable CI run):

```json
{
  "schemaVersion": 1,
  "name": "release",
  "stages": [
    {
      "id": "debug",
      "kind": "workflow",
      "workflow": { "manifest": "examples/hello/workflow.json" }
    },
    {
      "id": "ci",
      "kind": "github.actions",
      "dependsOn": ["debug"],
      "githubActions": {
        "mode": "attach",
        "repository": "cpp20120/CMakeWorkflow",
        "workflow": "ci.yml",
        "runId": 123456,
        "runAttempt": 1,
        "expectedHeadSha": "0123456789abcdef0123456789abcdef01234567"
      }
    }
  ]
}
```

Use `PipelineManifest.ParseJson(json)` and
`PipelinePlanner.Plan(document)`. The planner validates the entire finite graph (1–256 stages), case-sensitive stage
IDs, unique prerequisites, forward dependencies, cycles, stage option exclusivity, and GitHub attempt / commit binding
fields. It returns a detached, dependency-first plan. Deserialization rejects missing required and unknown JSON fields.

**Non-guarantees:** static validation does not read either manifest, verify a GitHub run, authorize a workspace path,
validate symlinks or credentials, capture artifacts, or guarantee idempotent external operations. Those checks belong to
a later Host-owned admission/execution adapter. Particularly, a GitHub run ID alone cannot establish commit or workflow
identity; the future adapter must verify all binding fields with GitHub.

Next patch: Host-owned pipeline records, child operation references, and read-only GitHub attach/watch with durable
waiting states. Dispatch and deployment should come only after recovery and authorization are defined.
