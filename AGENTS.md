# Analyzer suppression policy

- Do not generate suppressions from build diagnostics or add them merely to obtain a passing build.
- A compatibility rationale must identify a concrete external contract and explain what would break. The external
  .NET package boundary is Client/Contracts; public visibility inside implementation assemblies is not such a contract.
- Naming an unspecified existing API or claiming that JSON serialization requires mutable List properties is insufficient.
- For implementation DTOs, use a reviewed member-local design exception or CA1002/CA2227 configuration naming exact
  source files. Document the actual producer, mutations or ownership. Do not use assembly-wide or directory-wide disables.
- Fix parameter-name mismatches, unnecessary public CLI entry points and ordinary cancellation-token ordering instead
  of attributing them to hypothetical callers. Distinguish connection lifetime tokens from per-call cancellation.
- Keep strict Release warnings-as-errors and NativeAOT checks enabled. After API changes, update the relevant inventory;
  verify serialization/recovery behavior when collection or persistence contracts are affected.

Prefer native .NET testing infrastructure over ad hoc Python, Bash, or PowerShell test harnesses. Use the existing test framework for unit and integration tests, BenchmarkDotNet for managed microbenchmarks, and typed C# harnesses for end-to-end measurements. Shell scripts are permitted only as thin entry points or when testing shell-specific behavior. Do not implement assertions, benchmark aggregation, process orchestration, or test lifecycle management in shell scripts without explicit justification.

CMakeWorkflow is the default orchestration platform for its own engineering lifecycle. Prefer extending and consuming its existing capabilities over introducing ad hoc automation. Independent verification must use the established .NET testing infrastructure.

The existing functionality of CMakeWorkflow.
CMakeWorkflow.Scripting for programmable orchestration.
.NET test framework for correctness checks.
BenchmarkDotNet for managed microbenchmarks.
C# file-based apps for standalone auxiliary tools.
External scripting languages — only if there is a specific technical justification.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- When graphify-out/graph.json exists, run `graphify query "<question>"` before your first grep, search or source-file read for a codebase question, and put this rule in every subagent prompt that explores code. Read raw files only for lines the graph or an earlier read already located. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. When the answer is under a Markdown heading, `graphify section "<heading>"` prints that section's text, so the .md need not be opened. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
- A committed graphify-out/graph.json is rewritten whole on every build, so a large diff to it in a pull request is expected. Do not flag it as churn or suggest skipping the graph rebuild or its hooks.
