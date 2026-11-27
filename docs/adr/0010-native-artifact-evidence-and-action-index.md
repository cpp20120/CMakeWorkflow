# ADR-0010 — Native artifact evidence and Action Cache metadata

Status: implemented for explicitly initialized indexed CAS namespaces.

## Format registry

| Contract | Identifier |
| --- | --- |
| Physical CAS | `cmakeworkflow-artifact-cas-v3` marker for indexed stores |
| Document catalog | Existing SQLite application ID `0x434d5744`, catalog epoch 3 |
| Mutable reference index | `actions/index.sqlite`, application ID `0x434d5741`, epoch 1 |
| Action key policies | 1 for existing workloads, 2 for manifest-mapped source workloads |
| Artifact manifest | `Schema/artifactEvidence.capnp`, canonical unpacked frame, version 1 |
| Manifest identity | Exact SHA-256 of the canonical framed bytes in the shared physical CAS |

`cas STORE init-indexed` initializes an indexed namespace explicitly. Existing CAS namespaces preserve their marker and reference representation. An executable that supports only marker v1 or v2 refuses v3 before collection. Moving references into a freshly initialized namespace requires separately verified import. Existing compatible action entries remain available in their original namespaces.

## Publication and retention

The reference index uses strict SQLite tables, full synchronous commits, and the existing interprocess artifact namespace lease. A cache entry binds an ActionKey and key policy to one exact output blob digest and size. Effective invocation, declarations, input hashes, environment, toolchain, tree generation and dependency identity determine the ActionKey independently of its storage format. The source-bound policy additionally includes the captured source ID and effective mapped paths. The artifact output still uses SHA-256.

The publisher verifies and installs the physical blob before committing the action row. An interrupted write before row commit can leave an unreachable blob for the existing collector. Equivalent publication keeps the row and conflicting publication reports an error. A lookup checks policy and validates data before delivering a hit. Eviction removes the row under the namespace lease and makes the blob collectible when no other root protects it.

The artifact manifest records workspace, operation, Host, exact producing report hash and ordered artifact descriptors. Each descriptor includes the artifact ID, role, path, media type, digest and size. Stash validates the required payloads before publishing the manifest and establishing the indexed operation reference. The manifest remains rooted after unpinning. The pin controls retention of output blobs. Re-pinning requires the payloads to be available and verified.

The existing collector takes the shared lease and enumerates document roots, cache roots and artifact manifests before pruning. Missing or unrecognized index metadata, unexpected legacy references in an indexed namespace and invalid manifest evidence stop collection. This covers one physical CAS. Lore and the tooling store still have their own owners and retention rules.

## Evidence ownership

The manifest is evidence for the collected artifact set. Its report hash refers to the existing workflow evidence. Public artifact JSON projects that hash and the manifest BlobId. Execution progress, terminal reports, benchmark observations, profiler metadata, workflow and process journals, and History imports retain their current formats and authorities. A native History evidence link and a transactional checkpoint and journal design remain open.
