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
   - `/api/health/status`
   - `netclaw daemon status` (if CLI available)

## Direct Host Upgrade

`netclaw update` preserves the existing lifecycle owner: a daemon managed by an
active or enabled systemd user unit is stopped and restarted with
`systemctl --user`, while a directly-started daemon uses `netclaw daemon` process
control.

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
    - `netclaw status`
    - `curl http://127.0.0.1:5199/api/health/ready`
    - `curl http://127.0.0.1:5199/api/health/status`

## Rollback Notes

- Rollback is binary rollback plus, if needed, restoring a DB backup.
- Schema migrations should be treated as forward-only unless explicitly
  documented otherwise.
- For releases with incompatible schema changes, publish a release note entry
  with explicit rollback guidance.

## Version-Specific Upgrade Notes

### Trust-context hardening (issue #994)

The trust-context hardening change makes the `audience` and `boundary` trust
fields **required** on persisted background-job and reminder documents. A
job/reminder JSON document written by an older daemon predates these fields.

**Symptom:** after upgrading, a background job or reminder that previously ran
silently stops being scheduled.

**Cause:** the daemon rejects any persisted `BackgroundJobDefinition` or
`ReminderDefinition` document that lacks `audience` or `boundary`. On load it
logs an error naming the file and the missing field(s), excludes the document
from scheduling, and **preserves the file on disk** for inspection. The trust
context is not substituted — a job or reminder with no persisted audience
cannot be run safely, so no audience is invented.

**Remedy:** for each rejected document under `~/.netclaw`, either:

- recreate the job/reminder through the daemon (it will be persisted with
  explicit trust fields), or
- hand-edit the JSON document to add explicit `audience` and `boundary`
  values, then restart the daemon.

Check the daemon logs after upgrade for `predates issue #994` error entries to
find affected files. There is no automatic backfill and no `doctor` fix for
this case.

## Release Checklist (Pre-Distribution)

- migration scripts are idempotent and versioned
- startup migration tested on existing DB from previous release
- upgrade and rollback steps validated on:
  - Docker deployment path
  - direct host/systemd path
