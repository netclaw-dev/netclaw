# Release image for netclawd: the Netclaw autonomous operations daemon.
#
# Supports multi-arch builds (linux/amd64, linux/arm64) via buildx.
# For multi-arch, pre-built binaries are laid out as:
#   publish/cli-{amd64,arm64}/netclaw
#   publish/daemon-{amd64,arm64}/netclawd
# The TARGETARCH arg (injected by buildx) selects the right directory.
#
# For single-arch local builds (build-image.sh), the binaries are at
# publish/cli/netclaw and publish/daemon/netclawd; the build script
# hard-link-copies them to publish/cli-${arch}/ and publish/daemon-${arch}/
# and passes --build-arg TARGETARCH=$arch explicitly so the same Dockerfile
# works for both buildx (multi-arch) and plain `docker build` (single-arch).
#
# Why TARGETARCH has no default value (#1091): buildx auto-injects the
# predefined TARGETARCH per-platform, but ONLY if the in-stage `ARG`
# declaration has no default. Writing `ARG TARGETARCH=amd64` makes the
# default win over buildx's per-platform injection, so the arm64 manifest
# silently ends up with amd64 binaries. Leaving it bare is the canonical
# Docker pattern; failures when neither buildx nor --build-arg supply a
# value are intentional ("no silent fallbacks" — see CLAUDE.md).
#
# tini runs as PID 1 — it reaps orphaned zombies (netclawd's tool subprocesses
# that reparent to PID 1) and forwards signals — and launches entrypoint.sh,
# which supervises the netclawd process, restarting it on exit so the container
# survives config-update restarts and `netclaw init` wizard flows.
#
# The daemon runs as a dedicated non-root user (netclaw, UID 1654) for
# defense-in-depth. The daemon fails loudly when required
# configuration is missing (no silent fallbacks). Operators supply
# provider, model, and identity configuration via:
#   * NETCLAW_*-prefixed environment variables at `docker run` time
#   * A bind mount or volume at /home/netclaw/.netclaw (identity, config, data)
#
# The companion `netclaw` CLI binary is also installed so shell_execute
# can invoke it from inside the container (e.g., `netclaw doctor`).
ARG TARGETARCH
ARG NETCLAW_VERSION=unknown

FROM ubuntu:24.04
ARG TARGETARCH
ARG NETCLAW_VERSION=unknown

# Pre-install the general-purpose toolkit an autonomous agent is expected
# to reach for via its shell_execute tool. An Ubuntu base (not a chiseled
# runtime) is used deliberately so additional tools can be added at runtime
# for high-permissions use cases the agent discovers on its own — the
# pre-installed set is what the agent should have on hand without needing to
# install anything first.
#
# NOTE: the daemon runs as the non-root `netclaw` user, so it cannot
# `apt-get install` (no root) or write system PATH dirs at runtime. Agent-driven
# runtime installs must target the user-writable, on-PATH locations created
# below (~/.local/bin, ~/.dotnet, ~/.dotnet/tools) — e.g. `dotnet-install.sh`
# defaults to ~/.dotnet, `pip install --user` lands in ~/.local/bin. System-wide
# `apt-get install` remains an operator action (`docker exec -u root`, or baking
# a derived image).
#
# Categories:
#   TLS + HTTP:  ca-certificates, curl, wget
#   Process/OS:  procps
#   Data/JSON:   jq, sqlite3
#   VCS/GitHub:  git, gh
#   Python:      python3, python3-pip, python3-venv
#   Privileges:  gosu (entrypoint ownership repair, then drop to netclaw)
#   Init/PID 1:  tini (reaps orphaned subprocess zombies, forwards signals) — https://github.com/krallin/tini
#                Unpinned like the rest of this apt set; ubuntu:24.04 pins it at 0.19.0 for the LTS life.
#   Crypto:      libsodium23 (required by NSec for Ed25519 signature verification)
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      wget \
      procps \
      gosu \
      tini \
      git \
      jq \
      sqlite3 \
      python3 \
      python3-pip \
      python3-venv \
      gh \
      libicu74 \
      libsodium23 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/netclaw

# Self-contained single-file binaries. TARGETARCH selects the right
# platform directory (amd64 or arm64). Schemas are embedded in the CLI binary.
COPY publish/cli-${TARGETARCH}/netclaw /opt/netclaw/cli/netclaw
COPY publish/daemon-${TARGETARCH}/netclawd /opt/netclaw/daemon/netclawd
COPY docker/entrypoint.sh /opt/netclaw/entrypoint.sh
# `netclaw` on PATH is a self-dropping launcher (not a bare symlink) so a
# root-context `docker exec`/`kubectl exec -- netclaw <cmd>` re-execs as the
# netclaw user — see docker/netclaw-cli-launcher.sh and ADR-004. netclawd stays a
# plain symlink: it is only ever launched by the supervised entrypoint, which has
# already dropped to netclaw, so it never runs the root path.
COPY docker/netclaw-cli-launcher.sh /usr/local/bin/netclaw

RUN chmod +x /opt/netclaw/cli/netclaw /opt/netclaw/daemon/netclawd /opt/netclaw/entrypoint.sh /usr/local/bin/netclaw \
 && ln -s /opt/netclaw/daemon/netclawd /usr/local/bin/netclawd

# Non-root runtime user — limits blast radius of daemon-owned tool execution.
# The entrypoint starts as root only to repair bind-mount ownership, then
# re-execs itself as this user before launching netclawd.
# Pre-create the data and tools directories so image-owned paths inherit
# correct ownership when operators use Docker named volumes. ~/.local/bin and
# ~/.dotnet(/tools) are the user-writable, on-PATH targets for agent-driven
# runtime tool installs (the daemon is non-root and cannot write system dirs).
RUN groupadd --gid 1654 netclaw \
 && useradd --uid 1654 --gid netclaw --create-home netclaw \
 && mkdir -p /home/netclaw/.netclaw /tools \
            /home/netclaw/.local/bin /home/netclaw/.dotnet/tools \
 && chown -R netclaw:netclaw /home/netclaw/.netclaw /tools /home/netclaw/.local /home/netclaw/.dotnet

ENV HOME=/home/netclaw
ENV NETCLAW_HOME=/home/netclaw/.netclaw
ENV NETCLAW_DAEMON_PATH=/opt/netclaw/daemon/netclawd
# Make the user-writable install locations (and a mounted /tools/bin toolset)
# resolve as bare commands in the non-root agent's non-interactive shells, which
# never source ~/.bashrc/~/.profile. dotnet-install.sh -> ~/.dotnet; .NET global
# tools -> ~/.dotnet/tools; pip --user -> ~/.local/bin; operator/init-container
# toolsets mounted at /tools -> /tools/bin.
ENV PATH=/home/netclaw/.local/bin:/home/netclaw/.dotnet:/home/netclaw/.dotnet/tools:/tools/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
# Block in-place binary self-update — the image tag IS the version.
# Update availability checks still run so operators see when a new version exists.
ENV NETCLAW_Daemon__DisableSelfUpdate=true
# Declare that an external supervisor (entrypoint.sh, PID 1) owns the daemon
# lifecycle. The CLI refuses to spawn a detached netclawd in this mode so
# `netclaw init` / `netclaw daemon start` cannot split-brain with the
# supervised daemon (#1279). Config changes are applied via an in-process
# restart through the running daemon instead.
ENV NETCLAW_CONTAINER_SUPERVISOR=entrypoint

# Persist identity, config, data, and session state on an operator-mounted
# volume. Real operators must `netclaw init` (or bind-mount a pre-initialized
# tree) before starting the container; the daemon will fail loudly if
# required config is missing.
VOLUME /home/netclaw/.netclaw

EXPOSE 5199

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -sf http://127.0.0.1:5199/api/health/ready || exit 1

# Keep the image entrypoint root so it can repair host bind-mount ownership.
# docker/entrypoint.sh drops privileges before starting the daemon supervisor.
USER root

LABEL org.opencontainers.image.source="https://github.com/netclaw-dev/netclaw" \
      org.opencontainers.image.title="netclawd" \
      org.opencontainers.image.description="Netclaw autonomous operations daemon" \
      org.opencontainers.image.version="${NETCLAW_VERSION}" \
      org.opencontainers.image.licenses="Apache-2.0"

# tini as PID 1 reaps zombies reparented from netclawd's tool subprocesses
# (entrypoint.sh only `wait`s its own direct child) and forwards signals for a clean
# `docker stop` (#1287). `-g` forwards to the whole process group, not just the direct
# child, so a stop tears down netclawd AND any in-flight tool subprocesses together
# instead of leaving grandchildren to linger until the stop-timeout SIGKILL.
ENTRYPOINT ["/usr/bin/tini", "-g", "--", "/opt/netclaw/entrypoint.sh"]
