# Release image for netclawd: the Netclaw autonomous operations daemon.
#
# Supports multi-arch builds (linux/amd64, linux/arm64) via buildx.
# For multi-arch, pre-built binaries are laid out as:
#   publish/cli-{amd64,arm64}/netclaw
#   publish/daemon-{amd64,arm64}/netclawd
# The TARGETARCH arg (injected by buildx) selects the right directory.
#
# For single-arch local builds (build-image.sh), the binaries are at:
#   publish/cli/netclaw
#   publish/daemon/netclawd
# TARGETARCH defaults to amd64, and the COPY falls through to the
# arch-suffixed path — the build script symlinks the single-arch
# layout to the expected path.
#
# A wrapper entrypoint (entrypoint.sh) runs as PID 1 and supervises the
# netclawd process, restarting it on exit so the container survives
# config-update restarts and `netclaw init` wizard flows.
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
FROM ubuntu:24.04

ARG NETCLAW_VERSION=unknown
ARG TARGETARCH=amd64

# Pre-install the general-purpose toolkit an autonomous agent is expected
# to reach for via its shell_execute tool. An Ubuntu base (not a chiseled
# runtime) is used deliberately so operators can `apt-get install`
# additional tools at runtime for high-permissions use cases the agent
# discovers on its own — the pre-installed set is what the agent should
# have on hand without needing to install anything first.
#
# Categories:
#   TLS + HTTP:  ca-certificates, curl, wget
#   Process/OS:  procps
#   Data/JSON:   jq, sqlite3
#   VCS/GitHub:  git, gh
#   Python:      python3, python3-pip, python3-venv
#   Crypto:      libsodium23 (required by NSec for Ed25519 signature verification)
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates \
      curl \
      wget \
      procps \
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
# platform directory (amd64 or arm64).
COPY publish/cli-${TARGETARCH}/netclaw /opt/netclaw/cli/netclaw
COPY publish/cli-${TARGETARCH}/Schemas/ /opt/netclaw/cli/Schemas/
COPY publish/daemon-${TARGETARCH}/netclawd /opt/netclaw/daemon/netclawd
COPY docker/entrypoint.sh /opt/netclaw/entrypoint.sh

RUN chmod +x /opt/netclaw/cli/netclaw /opt/netclaw/daemon/netclawd /opt/netclaw/entrypoint.sh \
 && ln -s /opt/netclaw/cli/netclaw /usr/local/bin/netclaw \
 && ln -s /opt/netclaw/daemon/netclawd /usr/local/bin/netclawd

# Non-root runtime user — limits blast radius of tool execution and
# satisfies CIS Docker Benchmark / K8s PodSecurityStandards.
# Pre-create the data directory so VOLUME inherits correct ownership.
RUN groupadd --gid 1654 netclaw \
 && useradd --uid 1654 --gid netclaw --create-home netclaw \
 && mkdir -p /home/netclaw/.netclaw \
 && chown netclaw:netclaw /home/netclaw/.netclaw

ENV NETCLAW_DAEMON_PATH=/opt/netclaw/daemon/netclawd
# Block in-place binary self-update — the image tag IS the version.
# Update availability checks still run so operators see when a new version exists.
ENV NETCLAW_Daemon__DisableSelfUpdate=true

# Persist identity, config, data, and session state on an operator-mounted
# volume. Real operators must `netclaw init` (or bind-mount a pre-initialized
# tree) before starting the container; the daemon will fail loudly if
# required config is missing.
VOLUME /home/netclaw/.netclaw

EXPOSE 5199

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -sf http://127.0.0.1:5199/api/health/ready || exit 1

USER netclaw

LABEL org.opencontainers.image.source="https://github.com/netclaw-dev/netclaw" \
      org.opencontainers.image.title="netclawd" \
      org.opencontainers.image.description="Netclaw autonomous operations daemon" \
      org.opencontainers.image.version="${NETCLAW_VERSION}" \
      org.opencontainers.image.licenses="Apache-2.0"

ENTRYPOINT ["/opt/netclaw/entrypoint.sh"]
