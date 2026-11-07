# Recovery, remote Host, and artifact downloads

## Interrupted local workflow

**Do not interpret a crash, lost stream or timeout as proof that work never started.** Read saved reports and external state first.

```sh
CMakeWorkflow workflow report RUN_DIRECTORY --json
# Only if explicitly requested to inspect/resume the interrupted workflow:
CMakeWorkflow workflow resume RUN_DIRECTORY
```

For an interrupted `running` checkpoint, first `resume` quarantines uncertain effects as `outcome_unknown` (exit 4), **without dispatching work**. A second `resume` on that unknown checkpoint is rejected. `run.json` is the authoritative checkpoint.

Before reconciliation, verify whether the old child process has truly stopped, what external effects happened, what artifacts/logs remain, and whether repeating work can be safe. The exact `--expected-checkpoint` value is the SHA-256 of the **raw `run.json` bytes**, never a reserialized JSON object. Do not issue any decision unless the user authorizes the disposition and you have evidence. Examples of the operator-only procedure:

```sh
CMakeWorkflow workflow reconcile RUN_DIRECTORY --node NODE \
  --decision retry --reason "Old process confirmed stopped and partial effects reviewed" \
  --expected-checkpoint EXACT_RAW_RUN_JSON_SHA256 --confirm-stopped
# After *every* reconciliation refresh the report and SHA before a second decision.
# When all unknown nodes have been reconciled and explicit resume is wanted:
CMakeWorkflow workflow resume RUN_DIRECTORY
```

`--confirm-stopped` is an **operator assertion**, not a detection switch. A decision neither starts a new process nor rolls back effects. Successful work/manifest/outputs can still be rejected if identity changed. Do not invent timestamps, exit codes, or exactly-once semantics.

## Remote workflow with existing persistent Host (OpenSSH)

Example command shape (all placeholders must be validated, including remote paths and request ID):

```sh
# Existing remote Linux Host is started separately on the remote machine:
CMakeWorkflow host serve --endpoint /home/me/.cmwf/host.sock

# Controller commands (repeating the same transport parameters):
CMakeWorkflow remote find --destination devbox \
  --endpoint /home/me/.cmwf/host.sock --workspace /home/me/project \
  --request-id STABLE_REQUEST_ID
CMakeWorkflow remote status --destination devbox \
  --endpoint /home/me/.cmwf/host.sock --workspace /home/me/project \
  --operation OPERATION_ID
CMakeWorkflow remote inspect --destination devbox \
  --endpoint /home/me/.cmwf/host.sock --workspace /home/me/project \
  --operation OPERATION_ID
CMakeWorkflow remote artifacts --destination devbox \
  --endpoint /home/me/.cmwf/host.sock --workspace /home/me/project \
  --operation OPERATION_ID
CMakeWorkflow remote download --destination devbox \
  --endpoint /home/me/.cmwf/host.sock --workspace /home/me/project \
  --operation OPERATION_ID --artifact-id CATALOG_ARTIFACT_ID --output ./artifact.out
```

- `remote start` (mutating) needs `--request-id ID`, remote `--manifest FILE`, **SHA-256 of exact remote manifest bytes**, remote `--results-dir DIR`, optional `--jobs N`, and all connection options. **Do not start** unless requested and all are real/verified. Reuse a stable request ID for lookup; never generate a new ID to bypass an uncertain dispatch.
- On a lost `remote start` reply: first `remote find ... --request-id SAME_ID`, then `remote inspect`/`status`/`events`/`report`. No auto-reconnect, no auto-resubmit. A missing lookup alone does not prove the external operation never ran.
- `remote events --after SEQUENCE --limit N` reads a bounded page, not a long-lived subscription. Track cursors; validate durable status separately.
- Remote `--manifest`, `--results-dir`, `--endpoint` and `--workspace` refer to the **remote** filesystem. `remote download --output` is a **local** file path. Its parent must exist and destination must not. A resumed SDK download may reuse its checked `.cmwf.part` state, but do not silently retry a failed transfer.
- Remote artifact source must be a durable operation-bound catalog entry, not an arbitrary path or display name. Do not use GitHub artifact archive names to infer attempt identity.
- Host lifetime is separate from an SSH bridge. Closing the client/SSH connection does not automatically cancel accepted durable operations; the persistent Host must exist already.
- The CLI also supports a pinned-key `--transport sshnet ... --host --user --key-file --host-key-sha256` mode. Do not invent or bypass host-key pins; never print key contents.
- `remote cancel` mutates the operation and needs the **current** recovery revision from `remote inspect`. Do not guess the revision or cancel because a local timeout was seen.

## Sources

- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/recovery.md`
- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/remote-cli.md`
- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/remote-artifact-download.md`
