#!/bin/sh
set -eu

# The integration harness mounts a freshly generated authorized key and the
# published CLI. No key is baked into this image.
test -r /run/cmwf-ssh-test/authorized_keys
test -x /opt/cmwf/CMakeWorkflow

install -d -m 0700 -o cmwf -g cmwf /home/cmwf/.ssh
install -m 0600 -o cmwf -g cmwf \
  /run/cmwf-ssh-test/authorized_keys /home/cmwf/.ssh/authorized_keys
install -d -m 0700 -o cmwf -g cmwf \
  /home/cmwf/private /home/cmwf/workspace
install -d -m 0755 /run/sshd

# openssh-server's package installation may have generated build-layer keys;
# replace them on EVERY container start so the trusted pin is per-test-run.
rm -f /etc/ssh/ssh_host_*_key /etc/ssh/ssh_host_*_key.pub
ssh-keygen -q -t ed25519 -N '' -f /etc/ssh/ssh_host_ed25519_key
chmod 0600 /etc/ssh/ssh_host_ed25519_key

# This persistent Host is deliberately independent of SSH exec sessions.
# All workflow files remain in the unprivileged user's private workspace.
runuser -u cmwf -- /opt/cmwf/CMakeWorkflow host serve \
  --endpoint /home/cmwf/private/host.sock &

exec /usr/sbin/sshd -D -e -f /etc/ssh/sshd_config_cmwf
