FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src
COPY . .

RUN dotnet restore Netclaw.slnx
RUN dotnet publish src/Netclaw.Cli/Netclaw.Cli.csproj -c Release -o /out/cli
RUN dotnet publish src/Netclaw.Daemon/Netclaw.Daemon.csproj -c Release -o /out/daemon

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl procps \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/netclaw

COPY --from=build /out/cli/ /opt/netclaw/cli/
COPY --from=build /out/daemon/ /opt/netclaw/daemon/

RUN ln -s /opt/netclaw/cli/netclaw /usr/local/bin/netclaw \
    && ln -s /opt/netclaw/daemon/netclawd /usr/local/bin/netclawd

ENV NETCLAW_DAEMON_PATH=/opt/netclaw/daemon/netclawd

ENTRYPOINT ["sleep", "infinity"]
