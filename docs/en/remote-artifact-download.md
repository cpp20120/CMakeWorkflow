# Remote SDK downloads (Stage D3)

After a remote workflow has finalized, retrieve its catalog through the **same
persistent Host and JSON-RPC protocol** used by execution. File paths in the
catalog are informational; `artifactId` is the only remote selector.

```csharp
await using var client = await CMakeWorkflowClient.ConnectSshAsync(options, ct);
var operation = client.Operations.GetHandle(savedOperationId);
var catalog = await operation.ListArtifactsAsync(ct);
var stdout = catalog.Artifacts.Single(x => x.Kind == "stdout");
var saved = await operation.DownloadArtifactAsync(stdout, localDestinationPath, ct: ct);
```

A new local file is published only after the full SHA-256 matches the catalog.
The target directory must exist and the destination must NOT exist: no overwrite.
Partial data stays in `<target>.cmwf.part`; the SDK commits a verified checkpoint
in `<target>.cmwf.part.meta` every 1 MiB (and at EOF). Each checkpoint is bound
to the operation ID, artifact ID, declared size, modification timestamp and
SHA-256. The checkpoint contains an offset and SHA-256 of the committed local
prefix. Byte flush precedes the checkpoint rename. A crash may leave an
uncommitted tail, which is truncated to the verified prefix during resume.

If SSH disconnects or the download is canceled, create a **new SDK connection**
and call `DownloadArtifactAsync` for the same operation ID, artifact entry and
local destination. The SDK does **not** reconnect or redispatch work by itself.
An artifact identity mismatch refuses to reuse the partial files. Invalid
prefix/chunk/final hashes delete the untrusted partial and checkpoint; remote
transport failures retain resumable state. The final local rename is atomic
within one filesystem and does not overwrite an existing destination.

For deterministic integration coverage on POSIX with local IPC, CMake and Ninja:

```sh
dotnet build CMakeWorkflow.Host.Tests/CMakeWorkflow.Host.Tests.csproj -c Release
timeout --kill-after=5s 100s dotnet \
  CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll \
  --ssh-download-tests
```

The fixture tests an interrupted 3 MiB log transfer across two SSH bridge
connections, a poisoned partial, mismatched checkpoint identity, mismatched final
hash, existing-target protection, and a run.json download. The fixture still
substitutes a POSIX local shell for a real sshd network hop. A completed test
run is not proof of end-to-end security against concurrent hostile filesystem
mutations: D2 currently validates the path before opening and does not bind a
stable file descriptor identity across all requests. Harden this independently
before advertising a malicious multi-user Host.


For clients with JavaScript number semantics, Hosts advertising
`operations/artifacts/timestampText` return `lastWriteUtcTicksText` in both the
catalog and chunk response. Send the exact decimal string as
`expectedLastWriteUtcTicksText` when reading a chunk, leaving the numeric
`expectedLastWriteUtcTicks` at zero or omitted. Existing clients may continue
to send exact 64-bit numeric ticks.
