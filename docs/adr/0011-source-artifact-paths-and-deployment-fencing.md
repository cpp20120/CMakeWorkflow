# ADR-0011 — Source artifact paths and deployment fencing

Status: source path policy and read-only storage inspection implemented. Workspace conversion and coordinated backup remain open.

## Source-bound artifact paths

Binary execution ledger epochs 4 and 5 support manifest artifact paths. Newly initialized workspaces use epoch 5. Its resolved dispatch binding can carry manifest artifact policy 1, the captured input prefix, and the operation-owned output root. Existing epoch-2 and epoch-3 ledgers retain their original policy and dispatch identities. Binaries from earlier epochs refuse these layouts before execution.

Declared inputs identify individual captured files using portable, manifest-relative paths and must match the captured digest. A source-relative manifest directory must fall inside the captured source. A declared output occupies a logical path outside the captured file and directory namespace. Output ancestors and file access are checked inside the operation's results directory. The run and profile invocation paths support whole-argument references using `@input:N` and `@output:NAME`. The evidence catalog checks recorded output digests against the mapped files. Recovery resolves the same source snapshot and paths.

Directory inputs, paths outside the snapshot, arbitrary custom working directories, profile snapshot paths and substitutions inside arguments are outside this policy. An action may observe absolute paths. The cache includes effective absolute paths and gives each distinct execution path its own identity. This preserves correct cache behavior when the source or results root changes. Path checks protect the declared artifact boundary. Executing a trusted workflow still gives its child programs the normal operating-system permissions of the Host.

## Deployment

`workflow storage-inspect WORKSPACE` reads the workflow ledger and document catalog format identifiers and reports the associated store paths. It serves as an inspection command before an operator handles a copy. Creating a fresh binary ledger or indexed CAS is explicitly opt-in.

The execution ledger, document catalog and CAS payloads, and the operation results and stages must be kept together for recovery. Source-bound or provisioned workflows additionally depend on their exact Lore snapshots and tooling store contents. The currently rehearsed procedure stops the Host, copies the local fixture, and restores it to its original canonical path with matching executable and toolchain. The ledger stores path bindings. A live copy and restore to another path require a separate protocol. SQLite's full synchronous setting and filesystem replacement are used by the existing paths. Validation currently covers local Linux process termination. Power-loss, network filesystem and other platform semantics need independent verification.

There is currently no production in-place or copy-on-write workspace upgrade transaction. Keep an old executable paired with its original store and use fresh opt-in namespaces for testing. Opening a newer marker or ledger with an earlier executable is expected to fail before the fenced operation. Restoring a compatible original store is the rollback path for a deployment rehearsal. Existing historical identities and legacy read paths remain available.
