# Engineering Kits and performance analysis from C#

Use `EngineeringProject.OpenAsync` to discover an SDK project and read its SDK descriptor, Kit catalog, active Kit and profile names. Pass a `ProjectLocation` with `ProjectDirectory` to select an exact project. `InspectAsync(ConfigurationSelection)` gives the existing restore diagnostics, verified or proposed lock identities and resolved variants. Select a Kit for compiler, generator and platform. Select a profile, matrix, variant or overrides for configuration. Select a Client connection and workspace for placement.

Ordinary LINQ can build or filter `ConfigurationSelection` values. Pass each selection to `project.Operation` and use `DeveloperLifecycle.PlanOperationAsync` with the existing configure, build, test, run, benchmark, profile or package options. A benchmark on one Host can use the existing `KitMatrix` support with a common configuration selection. Its Kit branches have qualified identities, distinct build trees and serialized measurement nodes. When selections require different profiles, Hosts or independent operation types, prepare separate reviewed operations. Launch performance-sensitive operations sequentially unless your experiment policy permits interference. General Scripting fragments remain available for ordinary workflow authoring. Developer operation plans are reviewed artifacts with their own exact digest.

## Review, deliver and run

SDK, Kit and configuration plans retain their existing explicit approval APIs. Managed toolchain provisioning and environment lock publication and vcpkg dependency installation require their separate approvals. The workflow approval covers execution.

`ReviewedEngineeringWorkflow.MaterializeAsync` exposes a Scripting `WorkflowManifest` with the exact reviewed bytes. It can be exported with `WorkflowScript.ExportAsync` in a script authoring application. `PrepareAsync` verifies the original plan bytes, reevaluates the pinned selection and delivers the exact reviewed manifest through `WorkflowService.PrepareManifestAsync`. Changing the active Kit does not change the reviewed selection. Changes to inputs or locks that change the plan require a new review. `PreflightAsync` then asks the Host to validate its workspace, locks and toolchain. Persist your request ID before calling `StartAsync(preflight.CreateStartOptions(requestId))` and reconnect to the same workspace with `Operations.FindAsync(requestId)` after an uncertain reply.

For SSH, stage the SDK project, inputs and approved Kit and configuration evidence before calling Prepare. Pass the project's existing workspace-relative directory as the manifest base. Compiler paths, installations, environment locks and dependency receipts belong to the execution Host. Managed vcpkg receipts that bind a local workspace must be generated on that Host, or use the existing snapshot-based experiment path for portable staged evidence. See [Engineering Kits](engineering-kits.md) and [script manifest delivery](scripting.md#admission-remote-paths-and-recovery).

## Indexed evidence and bounded queries

Request a History policy explicitly at preflight. `WorkflowOperation.WaitForHistoryAsync` and `HistoryService.WaitForIndexAsync` poll independent states. **Disabled**, **Indexed** and **Failed** end the wait. **AwaitingFinalization**, **Pending** and **Indexing** continue until a terminal index state or token cancellation. Use a token deadline for a bounded wait. Reconnect using the durable operation ID to continue observation. Use `RetryIndexAsync` explicitly after resolving an indexing failure. Index retry imports verified evidence and does not dispatch workflow nodes.

`GetOperationRunsAsync` returns the committed association with the admitted database. The Host records the importer-provided run ID and source hash with index acknowledgement, verifies the final report and checks the indexed identity on lookup. The receipt can hold multiple runs. Current durable CMake indexing writes one run per workflow. Earlier indexed operations without receipts remain available by their known History run IDs. `GetMeasurementsAsync` and `QueryOperationAsync` retrieve the receipt's runs by identity. The queries accept metric, variant, scenario and a recorded build environment fingerprint. Independent operations in a campaign can be enumerated explicitly using their durable IDs.

`EnumerateRunPagesAsync` forwards the opaque cursor and filters and requires a page budget. Each request accepts a page size from 1 to 200. Consume the final page's `NextCursor` to continue. Pages are not transactionally isolated. Measurement reads have a 50,000-row cap, including operation-wide results, and fail on overflow. Narrow filters to obtain complete inputs. Separate measurement RPCs do not share a database snapshot.

## Summaries, comparison and gates

The example uses LINQ over raw typed measurements. `Summarize` groups by run, metric, unit, variant, scenario, method, workload, environment and declared direction and retains each sample. It reuses the descriptive statistics implemented for History comparison. Empty scalar input yields no summary. A singleton has zero population standard deviation. Non-finite samples are rejected.

`CompareAsync` accepts an optional `HistoryComparisonSelection` for metric, variant on each side, scenario and expected build environment identities. Host identity, recorded characteristics, units and workload still have to match. To intentionally vary compiler or configuration, set `AllowDifferentBuildEnvironments` and supply both expected fingerprints. The result identifies both runs, variants and environments, marks the difference intentional and supplies descriptive changes. The statistical `DetectAsync` retains its strict existing comparability and sample requirements. It supplies adjusted p-values and diagnostics for matching series.

`RegressionGate.Evaluate` uses median deterioration in the stated direction. Thresholds can use absolute metric units or percent of a positive baseline. Equality with the threshold passes. Missing evidence, insufficient samples, unexplained differences and nonpositive percentage baselines yield **Inconclusive**. Intentional build differences require separate opt-in at the gate. Set `RequireStatisticalEvidenceToFail` to require a matching History detection before treating above-threshold deterioration as failure. Within-tolerance results remain descriptive. `Aggregate` prioritizes failed gates, then inconclusive gates. A suggested CI mapping is 0 for Passed, 1 for Failed and 2 for Inconclusive. A workflow may execute successfully and still fail a performance gate.

## File-based campaign

Approve the `linux-gcc` and `linux-system-clang` Kits and their release configurations as described in the Kit guide. Then run from the repository root.

```sh
dotnet run --file examples/engineering/campaign.cs -- PROJECT performance-001
dotnet run --file examples/engineering/campaign.cs -- PROJECT performance-001 REVIEWED_SHA this kit-00-release kit-01-release
```

The first invocation prints the reviewed digest. The second uses the explicitly selected GCC series in the same indexed matrix as baseline. Pass a History run ID in place of `this` for comparison with an existing run. Select the recorded series names explicitly. Native benchmark timings determine the outcome.

The example uses the existing Developer project reference, which includes local Host integration. The distributable Scripting package still depends only on Contracts and Client. History querying and gates are available in the Client package without a Host implementation dependency. The `script` commands continue to treat file-based programs as authoring processes. This full campaign is a separate ordinary application.
