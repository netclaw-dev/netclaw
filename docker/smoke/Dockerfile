FROM ubuntu:24.04

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl procps \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/netclaw

# Self-contained binaries are published on the host and copied in
COPY publish/cli/netclaw /opt/netclaw/cli/netclaw
COPY publish/daemon/netclawd /opt/netclaw/daemon/netclawd

RUN ln -s /opt/netclaw/cli/netclaw /usr/local/bin/netclaw \
    && ln -s /opt/netclaw/daemon/netclawd /usr/local/bin/netclawd

ENV NETCLAW_DAEMON_PATH=/opt/netclaw/daemon/netclawd

ENTRYPOINT ["sleep", "infinity"]
