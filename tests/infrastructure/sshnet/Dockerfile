# Test fixture only: an actual sshd, a .NET 10 framework-dependent CLI and CMake/Ninja.
# Never deploy this image as a production Host.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
       openssh-server cmake ninja-build procps coreutils \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --shell /bin/sh cmwf \
    && echo 'cmwf:integration-fixture-password-auth-disabled' | chpasswd \
    && install -d -m 0755 /run/sshd /run/cmwf-ssh-test

COPY sshd_config /etc/ssh/sshd_config_cmwf
COPY entrypoint.sh /usr/local/bin/cmwf-sshd-entrypoint
RUN chmod 0755 /usr/local/bin/cmwf-sshd-entrypoint

EXPOSE 22
ENTRYPOINT ["/usr/local/bin/cmwf-sshd-entrypoint"]
