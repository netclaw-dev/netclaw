# Daemon Upgrade Runbook

This runbook describes how to upgrade `netclawd` safely while preserving local
state under `~/.netclaw`.

## Goals

- keep user/session state across upgrades
- apply schema migrations at daemon startup
- provide a clear rollback path

## Data and Binary Separation

- **Binaries**: `netclawd` / `netclaw` executable artifacts
- **Persistent data**: `~/.netclaw` (including `netclaw.db`, config, logs)

Upgrade reliability depends on replacing binaries while retaining persistent
data.

## Docker Upgrade

1. Stop old container.
2. Keep the existing persistent volume mounted at the same data path.
3. Start new image version.
4. Wait for startup migration + readiness:
   - `/api/health/ready`
   - `netclaw daemon status` (if CLI available)

## Direct Host Upgrade

1. Stop daemon:
   - `netclaw daemon stop`
   - or `systemctl --user stop netclaw`
2. Optional backup:
   - `cp ~/.netclaw/netclaw.db ~/.netclaw/netclaw.db.bak.$(date +%s)`
3. Replace binaries with new version.
4. Start daemon:
   - `netclaw daemon start`
   - or `systemctl --user start netclaw`
5. Verify health:
   - `netclaw daemon status`
   - `curl http://127.0.0.1:5199/api/health/ready`

## Rollback Notes

- Rollback is binary rollback plus, if needed, restoring a DB backup.
- Schema migrations should be treated as forward-only unless explicitly
  documented otherwise.
- For releases with incompatible schema changes, publish a release note entry
  with explicit rollback guidance.

## Release Checklist (Pre-Distribution)

- migration scripts are idempotent and versioned
- startup migration tested on existing DB from previous release
- upgrade and rollback steps validated on:
  - Docker deployment path
  - direct host/systemd path
