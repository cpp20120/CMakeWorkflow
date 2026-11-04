#!/usr/bin/env bash
set -Eeuo pipefail

# Real network/SSH.NET integration on a disposable localhost-only Docker sshd.
# No external credentials, Docker socket mount, privileged container, or TOFU.
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
for command in docker dotnet ssh-keygen ssh-keyscan timeout; do
  command -v "$command" >/dev/null || { echo "Missing prerequisite: $command" >&2; exit 2; }
done

tmp="$(mktemp -d)"
chmod 0700 "$tmp"
umask 077
container=''
image="cmwf-sshd-smoke-$(date +%s)-$$"
cleanup() {
  local code=$?
  trap - EXIT
  if [[ -n "$container" ]]; then
    if (( code != 0 )); then
      echo '--- Docker sshd/Host diagnostics ---' >&2
      docker logs "$container" >&2 || true
      docker exec "$container" sh -c 'cat /tmp/cmwf-host.log 2>/dev/null || true' >&2 || true
    fi
    docker rm -f "$container" >/dev/null 2>&1 || true
  fi
  docker image rm "$image" >/dev/null 2>&1 || true
  rm -rf -- "$tmp"
  exit "$code"
}
trap cleanup EXIT

# Independent ephemeral credentials. Only authorized_keys enters the container.
ssh-keygen -q -t ed25519 -N '' -f "$tmp/id_ed25519"
cp "$tmp/id_ed25519.pub" "$tmp/authorized_keys"

# The Docker image supplies the .NET runtime; the CLI is built on the host and
# mounted read-only. This tests actual SSH/network transport, not NativeAOT.
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 \
  --self-contained false -p:PublishAot=false -o "$tmp/publish"
test -x "$tmp/publish/CMakeWorkflow"
# Keep umask 077 for the private SSH key and temporary credentials, but the
# published CLI is mounted read-only and executed as the unprivileged cmwf user.
# It must be traversable/readable by that user; root's test -x alone is not enough.
chmod -R a+rX "$tmp/publish"
dotnet build CMakeWorkflow.Host.Tests/CMakeWorkflow.Host.Tests.csproj -c Release

docker build --quiet -t "$image" -f tests/infrastructure/sshnet/Dockerfile \
  tests/infrastructure/sshnet >/dev/null
container="$(docker run -d --publish 127.0.0.1::22/tcp \
  --mount "type=bind,src=$tmp/publish,dst=/opt/cmwf,readonly" \
  --mount "type=bind,src=$tmp/authorized_keys,dst=/run/cmwf-ssh-test/authorized_keys,readonly" \
  "$image")"

# Catch host-publish permissions immediately rather than waiting for an SSH
# readiness timeout. This checks the same Unix user used by the Host process.
if ! docker exec "$container" runuser -u cmwf -- test -x /opt/cmwf/CMakeWorkflow; then
  echo 'The published CLI is not executable by the container cmwf user' >&2
  exit 1
fi

binding="$(docker port "$container" 22/tcp | head -n 1)"
[[ "$binding" == 127.0.0.1:* ]] || { echo "Unsafe SSH publish binding: $binding" >&2; exit 1; }
port="${binding##*:}"
[[ "$port" =~ ^[0-9]+$ ]] || { echo "Invalid Docker SSH port: $binding" >&2; exit 1; }

# Confirm actual sshd/network readiness AND the independent persistent Unix
# socket. ssh-keyscan is ONLY a readiness probe, never a source of trust.
ready=0
for ((i=0; i<80; i++)); do
  if docker exec "$container" test -S /home/cmwf/private/host.sock >/dev/null 2>&1; then
    scan="$(ssh-keyscan -T 1 -p "$port" 127.0.0.1 2>/dev/null || true)"
    if [[ "$scan" == *ssh-ed25519* ]]; then ready=1; break; fi
  fi
  sleep 0.25
done
(( ready == 1 )) || { echo 'Docker SSH/Host readiness timed out' >&2; exit 1; }

# Pin obtained over local Docker control, not via an untrusted SSH handshake.
pin="$(docker exec "$container" ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub | awk 'NR==1 {print $2}')"
[[ "$pin" == SHA256:* ]] || { echo 'Cannot read trusted container host-key pin' >&2; exit 1; }

export CMWF_SSHNET_HOST=127.0.0.1
export CMWF_SSHNET_USER=cmwf
export CMWF_SSHNET_KEY="$tmp/id_ed25519"
export CMWF_SSHNET_PIN="$pin"
export CMWF_SSHNET_PORT="$port"
export CMWF_SSHNET_WORKSPACE=/home/cmwf/workspace
export CMWF_SSHNET_EXE=/opt/cmwf/CMakeWorkflow
export CMWF_SSHNET_ENDPOINT=/home/cmwf/private/host.sock

suite=CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll
for scenario in handshake workflow persistent; do
  echo "=== Real sshd / SSH.NET: $scenario ==="
  timeout --signal=TERM --kill-after=5s 150s dotnet "$suite" \
    --sshnet-live-lifecycle-tests "$scenario"
done

echo 'PASS real SSH.NET integration: host-key pin, authentication, private lifecycle, workflow cleanup, persistent reconnect'
