# Scheduling & Background Jobs

Scheduling is gated on `Scheduling.Enabled` in `netclaw.json` (default `true`).
When disabled, reminder tools are hidden, `ReminderManagerActor` skips startup
reconciliation, and fired reminders are acknowledged but not executed. Public
audience sessions cannot use scheduling tools regardless of the config flag.

`set_reminder` accepts three schedule types:

| Type | Examples |
|------|---------|
| `once` | `"30m"`, `"2h"`, `"2026-03-15T14:30:00Z"` |
| `interval` | `"30m"`, `"6h"`, `"1d"` |
| `cron` | `"0 */6 * * *"`, `"0 9 * * MON-FRI"`, `"CRON_TZ=Europe/Brussels 0 9 * * *"` |

### Cron time zones (`CRON_TZ`)

Cron schedules evaluate in **UTC by default**. To anchor a schedule to a local
time zone, prefix the expression with `CRON_TZ=<time-zone-id>` (Vixie crontab
syntax). The prefix is stored with the expression, so it survives reschedules
and daemon restarts, and it is DST-aware:

- `CRON_TZ=Europe/Brussels 0 9 * * *` — every day at 09:00 Brussels time
  (08:00 UTC in winter, 07:00 UTC during DST; transitions handled automatically).
- `CRON_TZ=America/New_York 0 9 * * MON-FRI` — weekdays at 09:00 New York time.

The time zone id must be an **IANA identifier without spaces** (e.g.
`Europe/Brussels`, `America/New_York`, `Asia/Tokyo`). Windows display names
such as `Eastern Standard Time` are not supported — the id ends at the first
space, so multi-word names resolve to a truncated, unknown identifier and the
reminder fails to schedule. When a user names a zone loosely ("Eastern time"),
translate it to the IANA id (`America/New_York`) before scheduling.

Delivery contract parameters:

- `delivery_kind`: required, one of `current_session`, `channel`, `none`
- `delivery_transport`: required when `delivery_kind=channel` (e.g. `slack`, `discord`, `mattermost`)
- `delivery_address`: required when `delivery_kind=channel` (`#channel`, `@user`, or canonical ID)
- `delivery_required`: optional bool, default `true`; set `false` only for audit/cleanup tasks
- `delivery_instructions`: optional content guidance only (never routing)
- `expires_in`: optional recurring-reminder expiry duration (for `interval`/`cron` only), e.g. `"24h"`, `"7d"`

You may also pass the structured form `delivery: { kind, transport?, address? }`
instead of the three flat delivery fields.

Rules:

- Always choose `delivery_kind` explicitly.
- Do not try to route via `delivery_instructions`.
- `current_session` is the session check-back path and should be preferred for
  conversational follow-ups in Slack/TUI/SignalR sessions.
- `channel` requires both transport + address and resolves names/handles to
  canonical IDs at set time; unresolved targets fail loud.
- Discord reminder targets must be explicit because channel IDs and user IDs are
  both snowflakes: use `channel:<channelId>` or `<#channelId>` for channel posts,
  and `dm:<userId>`, `@<userId>`, or `<@userId>` for DMs. Do not pass a bare
  Discord ID.
- `none` runs silently (history still records execution).
- `expires_in` is not valid for `once` reminders; omit it for one-shot schedules.
- For recurring reminders that are permanently complete (PR merged, deploy done,
  incident resolved), call `cancel_reminder` with that reminder's ID so it does
  not keep firing indefinitely.

`cancel_reminder` **disables** the reminder — it stops future executions but
preserves the definition file on disk for diagnosis and re-enablement. To
permanently delete a reminder and its history, use the CLI:

```
netclaw reminder delete <id>
```

The `cancel` CLI subcommand mirrors the tool behavior (disable only):

```
netclaw reminder cancel <id>     # disable, keep definition
netclaw reminder delete <id>     # permanent delete + history
```

Reminders that hit 5 consecutive execution failures are auto-disabled with a
`ReminderAutoDisabled` critical alert. The definition stays on disk so the
operator can diagnose and re-enable after fixing the root cause.

A known execution or delivery failure starts the Akka.Reminders retry policy.
The retry uses bounded backoff and the same durable occurrence identity. A
successful attempt resets the consecutive failure count.

A one-shot reminder stays enabled while an occurrence can retry. After a
successful acknowledgement, Netclaw deletes its definition and history. A poison
one-shot becomes disabled with a `Failed` outcome. Its definition and history
remain available until an operator uses the permanent delete command.
Startup reconciliation also removes completed one-shots from prior versions.

Each attempt has a 20-minute inactivity limit and a one-hour absolute limit.
The durable acknowledgement lease is 70 minutes. A daemon crash therefore lets
Akka.Reminders retry the occurrence after the lease expires.

**Failure visibility.** When a reminder execution fails for any reason — including
the 20-minute stall backstop that recovers a wedged run — the failure is posted
as a plain-language notice to the reminder's **destination channel** (for
`channel`-delivery reminders), so the operator sees it where they expect that
reminder's output. This is bounded by the auto-disable threshold (at most a few
notices plus the disabled notice), not the unbounded skip stream.

A one-shot that cannot start receives a negative acknowledgement. Akka.Reminders
then controls its retry delay. Netclaw acknowledges and skips a blocked recurring
occurrence. It does not keep a stale catch-up queue. The status command shows the
skip count:

```
netclaw reminder status <id>
```

`status` shows the enabled state, the terminal outcome, and current execution
state. It also shows the next fire, consecutive failures, skipped occurrence count,
and recent history. For one-shots, it shows the durable occurrence state, attempt
count, next retry time, and last failure reason.

Use this command when a reminder stops its expected work. A failure count that
increases usually means that the reminder or its delivery target is not healthy.

If `audience` is omitted during conversational scheduling, the reminder inherits
the audience of the channel/session that created it. A reminder cannot be
minted with broader audience than the creator currently holds; lowering the
audience is always allowed.

Other scheduling tools: `list_reminders`, `cancel_reminder`,
`get_reminder_history`.

## Proactive channel messaging

To start a brand-new conversation on a chat channel — a `delivery_kind=channel`
reminder firing, or unprompted cross-channel outreach ("let the team know") —
use the generic proactive-post tool:

- `send_channel_message(channel_key, destination, text)` posts through the
  selected enabled channel and creates a new conversation thread when that
  channel supports it.
- `destination` must be a resolved object with `channel_key`, `kind`, and `id`.
  Do not pass bare display names like `#general` or `@alice`.
- `destination.kind="destination"` posts to a channel/destination ID returned by
  `lookup_channel_destination`.
- `destination.kind="direct_message"` sends a DM using the stable user ID from
  `lookup_channel_user`; this is supported only by channels that advertise DM
  output (Slack, Discord, and Mattermost when enabled in config).
- Reminder- and webhook-originated turns may only call `send_channel_message`
  against the delivery target configured on the reminder or webhook route. If a
  trigger turn has no configured target, Netclaw fails loud instead of choosing a
  default output channel.

Use the generic lookup tools before sending when you do not already have a
stable channel/user ID:

- `lookup_channel_user(channel_key, query)` resolves users on enabled channels
  that support user lookup, currently Slack, Discord, and Mattermost.
- `lookup_channel_destination(channel_key, query)` resolves destinations on
  enabled channels that support destination lookup. Slack can resolve channel
  names and IDs; Discord can resolve channel mentions, stable IDs, and cached
  text-channel names; Mattermost requires an exact channel ID.
- **Discovery:** call `lookup_channel_destination` with `query` omitted (or
  blank) to list every destination the channel can currently deliver to —
  Slack lists the configured allowlist (the resolved default channel plus
  `AllowedChannelIds`) with display names resolved from the API and archived
  channels excluded, Discord lists ACL-allowed guild text channels, and
  Mattermost lists the configured allowlist. Use this to answer "where can I
  post?" before sending. Listing is destination-only: user directories are
  unbounded, so `lookup_channel_user` always requires a query.

Both tools require `channel_key` as the first argument. Use the returned
`channel_key` and `stable_id` exactly; for destination lookups, use the returned
`address_kind` (`destination`) as `destination.kind`. For user lookups, set
`destination.kind` to `direct_message` and pass the returned user `stable_id`.
If lookup is ambiguous, pick from the returned candidates instead of guessing.
Do not use channel-specific lookup aliases such as `lookup_slack_user` or
`lookup_mattermost_user`; lookup is intentionally routed through the generic
channel tools.

Examples:

```
send_channel_message(
  channel_key: "slack",
  destination: { channel_key: "slack", kind: "destination", id: "C0123ABC" },
  text: "Deployment finished successfully.")

send_channel_message(
  channel_key: "mattermost",
  destination: { channel_key: "mattermost", kind: "direct_message", id: "26characterMattermostUserId" },
  text: "Your report is ready.")

send_channel_message(
  channel_key: "discord",
  destination: { channel_key: "discord", kind: "direct_message", id: "123456789012345678" },
  text: "Your report is ready.")
```

## Approval Requirements for Reminders and Webhooks

Reminders and webhooks execute without a human present — they CANNOT prompt for
tool approval. The cwd at firing time will not match any cwd a user clicked
"Always here" for during interactive use, so folder-scoped approvals will not
match.

**Before creating a reminder that uses shell commands**, identify the verbs the
task will need (e.g. `freshdesk`, `curl`, `git pull`) and pre-approve them as
global wildcards. Two paths:

1. **Suggest `trust-verb` from the agent.** When you (the agent) are helping the
   user set up a scheduled task, identify the verbs the task will need and ask
   the user before pre-approving each one. Example:

   > "This reminder will need to call `freshdesk --since=24h` whenever it
   > fires. Mind if I pre-approve `freshdesk` as a global verb so the reminder
   > can run unattended? I'll do this with
   > `netclaw approvals trust-verb freshdesk`."

   On confirmation, run the trust-verb command via `shell_execute`. The grant
   becomes a `(verb, null)` entry — auto-approved for any cwd.

2. **Operator runs the CLI directly:** `netclaw approvals trust-verb <verb>`.
   Same outcome; useful when the user already knows what they want.

If the user has already trusted the verb in a previous session, no action is
needed — `(verb, null)` grants persist in `tool-approvals.json` across daemon
restarts.

**Path restrictions:** A trusted verb runs wherever the creating audience's
file-access policy allows — the same scoping `file_write` uses. Reminders and
webhooks run autonomously (no live human approver), so even a Personal one is
confined to an *autonomous zone* rather than the blanket access an interactive
Personal session gets. Inside that zone it can **read** its session directory,
the current project, and the shared read roots (skills, identity, workspaces),
and can **write** to its session directory, the current project, and the
**workspaces** directory — the designated working area for persisted state, so a
reminder can keep a dedup/state file there across runs. It cannot write outside
those — notably not to the system-managed skills or identity trees. A Team or
Public reminder/webhook is confined to its session directory and cannot run
`shell_execute` at all, since shell is Personal-only. Protected paths —
`secrets.json`, `.netclaw/keys`, `config/webhooks` — are always denied regardless
of audience or pre-approval.

**If a reminder fails with `command_not_pre_approved`:** The verb is not in the
approval store as a global wildcard. Run
`netclaw approvals trust-verb <verb>` and the next firing succeeds.

**If a reminder fails with `path_outside_trust_zone`:** The command targets a
path outside the allowed roots. Either move the target into a workspace, or ask
the user to add the path to trusted roots in config.

## Background Jobs


A background job is a **detached process with no expectation of completion** —
use it for anything that outlives a single tool call: long builds, test suites,
dev servers, watchers. Output streams to the job's log file while it runs, and
you are notified whenever the process terminates, by whatever cause.

To submit: set `_background: true` in the `shell_execute` tool call metadata.
Approval gates are evaluated before the job starts. The submit result includes
the output log path.

Lifecycle:

- **No `_timeout_seconds` = no kill timer.** The job runs until it exits, you
  cancel it, or the session passivates. A positive `_timeout_seconds` arms an
  explicit kill timer.
- **Jobs are killed when the session passivates** (conversation idle past the
  idle timeout). A job killed this way shows as `reaped` in
  `[active-background-jobs]` on your next turn — its process is gone; resubmit
  if still needed (its output log remains readable). For work that must survive
  an idle conversation, use a scheduled task; check-back reminders also keep
  the session (and its jobs) alive across a long wait.
- On daemon restart, running jobs are killed and you receive a `lost`
  notification with the log path — relaunch if still needed.
- Process exit (success or failure) delivers a result turn with exit code,
  output tail, and log path — even if the session was passivated mid-flight.
- Netclaw retains every terminal job definition and its logs for 24 hours after
  completion. The hourly cleanup sweep then deletes both artifact types.

Monitoring a running job (e.g. waiting for a dev server to come up):

- `file_read`/`grep` the output log — it streams live (secret-redacted,
  rotation-bounded) at `~/.netclaw/jobs/{id}/output.log`.
- `check_background_job(JobId: "id")` — status, elapsed time, live output tail
- Probe the service directly (e.g. curl the port) once the log shows it started.
- `check_background_job(JobId: "id", Cancel: true)` — cancel a running job.
  **Cancel servers and watchers when you are done validating** — do not leave
  them holding one of the 5 concurrent job slots.

Rules:

- Only `shell_execute` supports background mode. Other tools ignore `_background`.
- `_timeout_seconds` alone does NOT trigger background execution.
- For synchronous calls `_timeout_seconds` is honored as you set it (no ceiling
  or floor); when omitted, the default tool timeout applies. Background jobs
  differ: omitted means no timer at all.
- Synchronous tool timeouts are wall-clock budgets for opaque tools; repeated
  stdout/progress output does not extend them. `spawn_agent` is self-monitoring:
  the parent applies NO timeout to it at all — the subagent owns its liveness end
  to end (its own prefill/no-progress watchdog reports a stall, and it always
  returns a final result). A spawned subagent is bounded only by its own internal
  watchdog or by you cancelling the turn (send another message in the thread).
- **Long-running delegation calls** (e.g. `curl` to a local coding-agent or
  model server that takes minutes to respond) should run as background jobs.
- The user must approve the command before it starts running in the background.
- Maximum 5 concurrent background jobs; overflow queues FIFO.
- Job definitions persist to `~/.netclaw/jobs/{id}.json` until 24 hours after
  the job reaches a terminal state.

`check_background_job` is only available when shell execution is granted (same
`shell` grant category). It validates that the requesting session matches the
submitting session's audience and boundary.

After submitting a long finite job, schedule a check-back reminder so you report
results proactively when it completes.

Active background jobs appear in the `[active-background-jobs]` section of the
session context on every turn.
