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

## When a command refuses

`netclaw daemon stop`, `netclaw daemon start`, `netclaw daemon status`, `netclaw update`, and the factory
reset read the PID file `<home>/netclaw.pid`. They act on that PID only when it is shown to serve the home of
the command. When it is not, the command sends no signal, keeps the PID file, and prints two lines:

```text
Nothing was stopped. PID 4100 is not shown to serve /home/op/.netclaw-b.
The daemon at http://127.0.0.1:5300 did not confirm PID 4100 for this home. If no Netclaw daemon of this home
runs, delete /home/op/.netclaw-b/netclaw.pid. If PID 4100 is a daemon of this home, check it, then stop it
with 'kill 4100' (Task Manager on Windows).
```

`start` prints `Daemon was not started. PID <pid> is not shown to serve <home>.` and `status` prints
`PID <pid> is not shown to serve <home>.` Both exit with code 1. They send no notice, so their second line starts
with `No daemon of this home was proven to own PID <pid>.` and names no endpoint. The rest of the line is the same.

Choose the recovery by cause:

1. The PID file is stale for this home. Another home runs a daemon with that PID, or the OS reused the PID.
   Check which home the process serves: on Linux, run `tr '\0' '\n' < /proc/<pid>/environ | grep -E '^(NETCLAW_HOME|HOME)='`.
   If the process serves another home, delete `<home>/netclaw.pid` and run the command again. Never use `pkill netclawd`:
   it stops the daemon of every home.
2. The process is a wedged daemon of this home. On macOS and Windows the CLI cannot prove it, because the
   daemon does not answer and the OS gives no environment. Check that the process serves this home, then stop it
   by hand with `kill <pid>` (macOS) or Task Manager (Windows). On Linux the environment proves it, so `stop` works,
   except when the daemon runs with a relative `NETCLAW_HOME` or one with `~`, `$`, or `%`. Then use `kill <pid>` too.
3. The lock file is held and the PID file is missing. The message names `<home>/netclaw.lock`. Find the process
   that holds it (`lsof <home>/netclaw.lock` on macOS and Linux) and stop it with `kill <pid>`.
4. You update from a daemon that is older than this check, on macOS or Windows. The daemon answers 200 with no
   `home`, so the CLI cannot prove it. Stop the old daemon once with `kill <pid>`, then run the update again.

`netclaw daemon install` writes `Environment="NETCLAW_HOME=<home>"` into the `systemd` user unit, with the home of the
CLI that ran it (the path cannot hold whitespace, quotes, backslashes or `%`). Drop-ins from `systemctl --user edit`
are not read. There is one unit per user: to move it to another home, run `netclaw daemon uninstall` for the home it
serves, then `netclaw daemon install` for the new one. A unit that an older release wrote has no `NETCLAW_HOME` and
serves `<HOME>/.netclaw`, unless its `daemon.env` sets `NETCLAW_HOME`: then it serves that home, and
`netclaw daemon install` from that home refreshes it. On Linux the CLI proves which home the
daemon serves from the `NETCLAW_HOME` (or `HOME`) entry of the daemon environment.

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
the daemon does not acknowledge it.

Caution: the damaged file can hold a `Completed` record. Before you delete the file, read
`<id>.history.jsonl`. If it has an `ok` row for this fire time, repair the file with
`outcome: Completed` and do not delete it. A delete inside the window lets Akka.Reminders
deliver the occurrence again, and the one-shot runs again. A delete after the window ends the
one-shot as `Failed` with its prompt, although it ran.

Repair or delete the file in `ended/` within about 12 hours.
Akka.Reminders delivers the occurrence again after each acknowledgement lease (70 minutes), but
it stops after ten attempts. After that, the next sweep or restart ends the one-shot as `Failed`,
and the one-shot does not run. The daemon never overwrites an unreadable file. It skips the
one-shot and alerts again until the file is readable or gone.

**An end record that the daemon could not write:** the `reminder.settlement.failed` alert reads
"end record could not be written". The one-shot ran, and the daemon acknowledged the occurrence
so that it does not run again. Free the disk space. The next 12-hour sweep or the next restart
ends the one-shot. A restart is not needed. Do this within 12 days of the run, because
Akka.Reminders deletes the occurrence status after 12 days.

**A failed status query:** the `reminder.status.query_failed` alert reads "occurrence status".
The 12-hour sweep or the startup reconciliation could not read the Akka.Reminders status of a
past one-shot. The daemon leaves that one-shot as it is and does not restart. Check the
Akka.Reminders storage. The next sweep tries again. A one-shot that runs at that time gets no query.

**Rollback:** an older binary ignores the `ended/` folder and the history files that
have no definition. It does not create the deleted definitions again.

## Release Checklist (Pre-Distribution)

- migration scripts are idempotent and versioned
- startup migration tested on existing DB from previous release
- upgrade and rollback steps validated on:
  - Docker deployment path
  - direct host/systemd path
