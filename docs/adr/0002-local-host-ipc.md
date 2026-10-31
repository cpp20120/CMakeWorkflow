# ADR-0002: Local CMakeWorkflow Host

> **Language / Язык:** [English](../en/adr/0002-local-host-ipc.md) · [Русский](../ru/adr/0002-local-host-ipc.md)

Date: 2026-10-30. Status: accepted for the read-only Doctor prototype; broader
migration is deferred until the operation lifecycle is specified and tested.

## Context

CLI and VS Code need consistent environment diagnosis, future repair plans and
progress reporting. Existing ToolingService already owns probe and installation
semantics. A Host should expose that service, not reimplement provisioning or DAG
execution in the extension.

## Decision

Add a local Host entry point to the existing executable. Use StreamJsonRpc over
Unix sockets / Windows current-user named pipes, with vscode-jsonrpc in the
extension. CLI and VS Code are separate clients of the same service contract.
The first methods are initialize and tooling/doctor, with progress notifications
and cooperative request cancellation. Check NativeAOT publication and actual
cross-language traffic before migrating mutating operations.

Use source-generated target metadata, proxies and System.Text.Json DTO metadata.
Pin transport library versions. Keep normal CLI commands working independently.
The extension owns lazily started per-folder Hosts; a configured endpoint enables
connection to an externally owned Host. There is no global daemon discovery,
installer, background auto-update or service registration in this slice.

Local gRPC is a viable alternative, including Unix socket transport. It is not
rejected for being network-only. The cost here is maintaining a language-neutral
contract/client generation alongside code-first .NET APIs. JSON-RPC fits the
existing JSON DTOs and TypeScript client with less integration work. A future
network service would require a separate authentication and deployment design.

## Consequences and next boundary

We must own endpoint lifecycle, protocol compatibility, per-connection workspace
context, cancellation and process cleanup. A successful AOT build is insufficient:
serialization, error responses, notification framing and generated proxies must
be executed in tests. See [Host IPC](../host-ipc.md) for implemented behavior.

Doctor is read-only and connection-bound. Fix requires reviewed immutable or
revalidated plans, installation serialization, privilege handling and consistent
post-install tool selection. Durable workflow operation IDs, state queries and
reconnection semantics are separate future work. Do not silently retry builds or
infer that disconnect means cancellation for future mutating operations.

References: [StreamJsonRpc NativeAOT](https://microsoft.github.io/vs-streamjsonrpc/docs/nativeAOT.html),
[framing compatibility](https://microsoft.github.io/vs-streamjsonrpc/docs/extensibility.html),
[code-first gRPC and multiple languages](https://learn.microsoft.com/en-us/aspnet/core/grpc/code-first).

## Follow-up: explicit Fix (2026-10-30)

Implemented connection-bound planFix/applyFix, single-use expiring plans,
revalidation before mutation, per-user installation locking, progress and a
post-install Doctor check. VS Code requires explicit confirmation and then sets
only the selected workspace's child-process tool directory after verified success.
Cancellation can leave partial installation; no durable recovery or automatic
retry is claimed. Existing package managers and verified-release installer still
own installation mechanics. See the current Fix contract in [Host IPC](../host-ipc.md).
