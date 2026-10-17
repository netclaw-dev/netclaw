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

1. Stop old container. (`netclaw daemon stop` inside a running container only
   bounces the daemon: the container supervisor restarts it.)
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
   - `netclaw daemon stop` (goes through the systemd user unit when one is
     installed for the default `~/.netclaw` home)
   - or `systemctl --user stop netclaw`
2. Optional backup:
   - `cp ~/.netclaw/netclaw.db ~/.netclaw/netclaw.db.bak.$(date +%s)`
3. Replace binaries with new version.
4. Start daemon:
   - `netclaw daemon start` (starts the unit when one owns the daemon)
   - or `systemctl --user start netclaw`
5. Verify health:
    - `netclaw daemon status` (exits 1 until `/api/health/ready` answers)
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

### One-shot reminders delete themselves (issue #2526)

A one-shot reminder now deletes itself after it runs, after its failures reach the
limit, after it expires, or after you cancel it. Before this change, a spent
one-shot stayed as a disabled definition.

**What happens at the upgrade:** the daemon ends every leftover one-shot at
startup. A leftover is a one-shot with a recorded outcome, or a disabled one-shot
whose fire time has passed. No operator step is needed, and there is no prune
command. The 12-hour sweep repeats the same work.

**What stays:** a small end record for each ended one-shot, in
`~/.netclaw/schedules/reminders/ended/`, and its run history. Both stay for 12 days
after the end. A leftover that ended more than 12 days ago is removed in the same
pass. `netclaw reminder status <id>` and `netclaw reminder history <id>` work in
that period. `netclaw reminder list` does not show an ended one-shot.

**What changes for a failed one-shot:** the daemon deletes the definition. The end
record keeps the reminder prompt for 12 days, and only `netclaw reminder status <id>`
shows it. Copy the prompt into a new `set_reminder` call to run the reminder again.
Look for the `reminder.auto_disabled` alert and the channel notice.

**What stays the same:** a paused one-shot (`netclaw reminder disable`) with a future
fire time stays, and `netclaw reminder enable` can start it again. A recurring
reminder is never deleted on its own.

**An end record that the daemon cannot read:** the daemon raises the `reminder.tombstone.invalid`
alert. A read shows no history for that id. A fire of a one-shot with that id does not run, and
the daemon does not acknowledge it. Repair or delete the file in `ended/`. Akka.Reminders then
delivers the occurrence again, after the acknowledgement lease expires.

**Rollback:** an older binary ignores the `ended/` folder and the history files that
have no definition. It does not create the deleted definitions again.

## Release Checklist (Pre-Distribution)

- migration scripts are idempotent and versioned
- startup migration tested on existing DB from previous release
- upgrade and rollback steps validated on:
  - Docker deployment path
  - direct host/systemd path
