#!/bin/sh
# Self-dropping launcher for the netclaw CLI. See docs/adr/ADR-004-non-root-cli-self-drop.md.
#
# The daemon runs as the unprivileged `netclaw` user (defense-in-depth for an
# agent that executes model-chosen shell commands). But `docker exec` and
# `kubectl exec` default to the image's USER (root), and `netclaw init` — the
# documented first-run setup step — is normally invoked exactly that way.
#
# Two things break when the CLI runs as root in this image:
#   1. The CLI is a .NET single-file binary; it self-extracts into a per-$HOME
#      dir ($HOME/.net/netclaw/<hash>/) that the runtime locks to the invoking
#      user at mode 700. Extracted as root, the netclaw-user daemon can no longer
#      extract its own CLI -> "Failed to create directory ... Error code: 13"
#      (EACCES, exit 160).
#   2. `netclaw init` (and other config writes) persist identity/config/secrets
#      under NETCLAW_HOME. Written as root, the non-root daemon can't read them.
#
# The CLI never needs root, so if we are root we transparently drop to the
# netclaw user. This keeps `docker exec -- netclaw <cmd>` ergonomic across every
# orchestrator without operators needing to know about `gosu`/`-u netclaw`. When
# the image already runs as netclaw (e.g. the daemon's own shell_execute, or a
# deployment that sets runAsUser/-u netclaw) the check is false and we exec
# directly — no double drop.
set -eu

REAL=/opt/netclaw/cli/netclaw

if [ "$(id -u)" = 0 ]; then
    export HOME=/home/netclaw
    exec gosu netclaw "$REAL" "$@"
fi

exec "$REAL" "$@"
