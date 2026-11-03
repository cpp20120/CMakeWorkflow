# Future campaign recipes

Mutation, weak-memory and fuzz operations currently use their own typed campaign services and Host RPC methods. They
report structured outcomes and artifacts, and their cancellation is request-scoped. The generic workflow DAG does not
yet represent all of their inputs, output ownership, or recovery boundaries. Wrapping the CLI in an arbitrary shell node
would discard those contracts.

A future migration should first define native typed workflow operation kinds for each campaign step. The existing
planner must validate their inputs and artifact ownership; the existing durable Host must bind immutable requests,
checkpoint before external dispatch, and expose explicit cancellation and recovery decisions. Campaign executors would
then return structured results through the existing operation extension boundary, with report and History ingestion
defined separately. Only after those semantics exist should
`FuzzRecipeV1`, `MutationRecipeV1`, or `WeakMemoryRecipeV1` lower into the ordinary `WorkflowDocument` schema. Existing
campaign RPCs remain available throughout that migration.
