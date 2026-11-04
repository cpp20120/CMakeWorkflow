# Remote CLI (Stage E)

The `remote` commands are a **single-connection frontend** to the existing
`CMakeWorkflow.Client` SDK. They never start an SSH daemon, create a Host, upload
manifests or retry a lost RPC. A **persistent** Linux Host must already be
running and its Unix socket must be accessible to the SSH user.

All arguments are explicit, and output is JSON. The SSH destination is a host
alias or `user@host` interpreted by local OpenSSH configuration. The SDK uses
non-interactive `BatchMode=yes` and the Host's `host bridge --stdio` entrypoint.

```sh
# On remote machine (in another terminal/session):
cmakeworkflow host serve --endpoint /home/me/.cmwf/host.sock

# On controller: all paths besides --output are REMOTE paths.
base='--destination devbox --endpoint /home/me/.cmwf/host.sock --workspace /home/me/project'

# Read-only lookup after a lost start reply (or to recover a known operation):
cmakeworkflow remote find $base --request-id release-42
cmakeworkflow remote status $base --operation OPERATION_ID
cmakeworkflow remote inspect $base --operation OPERATION_ID
cmakeworkflow remote events $base --operation OPERATION_ID --after 0 --limit 100
cmakeworkflow remote report $base --operation OPERATION_ID
cmakeworkflow remote artifacts $base --operation OPERATION_ID
cmakeworkflow remote download $base --operation OPERATION_ID \
  --artifact-id stdout:build --output ./build.stdout
```

## Durable admission and cancellation

```sh
cmakeworkflow remote start $base \
  --request-id release-42 \
  --manifest /home/me/project/workflow.json \
  --manifest-sha256 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef \
  --results-dir /home/me/project/results/run-42 --jobs 2

cmakeworkflow remote cancel $base --operation OPERATION_ID --revision 0
```

The manifest digest above is **illustrative**. Obtain the actual SHA-256 of the
manifest **on the remote Host**, before admission, and persist the request ID.
After a lost `start` response, use `remote find` with the same ID. An unknown
request does not prove whether work occurred and does not authorize a new
request ID or an implicit resend. `cancel` requires the **current revision** from `remote inspect`; this CLI
never guesses the revision or automatically resubmits after a conflict.

`status`, `find`, `inspect`, `events`, `report` and `artifacts` make no mutation RPC.
`events` returns one bounded page with `nextSequence`, not an endless watch.
`download` uses the SDK's verified `.cmwf.part` checkpoint and SHA-256
publication: rerun the command explicitly after reconnect to resume.
Artifacts are fetched by **operation-bound ID**, never by arbitrary Host path.
The local destination must not exist, and its parent directory must exist.

Optional connection flags: `--ssh-executable PATH`, `--remote-executable PATH`,
`--connect-timeout-seconds N` (1–3600). OpenSSH authentication and host key
verification are supplied by the user's SSH configuration. No username,
password or private key is read from workflow JSON.
