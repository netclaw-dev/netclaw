# Behavior Debugging Runbook

Use this when Netclaw behavior is confusing (missing replies, duplicate replies,
message loops, dropped sessions, or unexpected policy decisions).

## Quick Triage

1. Confirm daemon status:

   ```bash
   netclaw daemon status
   ```

2. Confirm Slack session activity is being persisted:

   ```bash
   sqlite3 "$HOME/.netclaw/netclaw.db" \
     "select persistence_id, max(created), max(sequence_number) from journal group by persistence_id order by max(created) desc limit 10;"
   ```

3. If session rows are created but Slack has no reply, focus on outbound adapter
   path (Slack thread binding actor and Slack post client).

## Enable Debug Logging

Set these in `~/.netclaw/config/netclaw.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    },
    "Console": {
      "Enabled": true
    }
  }
}
```

Notes:

- `LogLevel:Default` is shared by MEL and Akka.NET logger integration.
- `Console:Enabled` prints logs to stdout/stderr for Rider/terminal debugging.

## What Healthy Slack Flow Looks Like

Expected sequence (same `SessionId` / `SlackThreadTs`):

1. `Routing Slack event ... to conversation ...`
2. `Routing Slack event ... to session thread actor`
3. `Accepted inbound Slack message for session queue`
4. Session actor logs `Received user message`
5. `Posted Slack reply message`

If steps 1-4 appear but step 5 does not, inspect Slack outbound posting path.

## Known Failure Signatures

- `Ignoring Slack event ... channel not allowed`
  - Channel ACL filter blocked event.
  - DM may be blocked by channel filtering if policy precedence is incorrect.

- repeated assistant replies / loops
  - Bot messages are being re-consumed as inbound events.
  - Ensure bot/self messages are ignored at conversation policy layer.

- `ThreadOutput ... unhandled` on `Flow-...-unknown-operation`
  - Stream callback captured wrong actor context.
  - Use captured `self` actor ref for stream-to-actor bridging.

- `CommandAck` dead letters
  - Stream fire-and-forget path without ack consumer.
  - Route commands through relay actor that no-ops `CommandAck`.

## Slack-Specific Checks

1. Verify bot can post to thread using direct API call.
2. Compare channel history vs thread replies to detect thread mismatches.
3. Confirm the bot user ID and mention parsing are correct.
4. Confirm session key is stable: `{channelId}/{threadTs}`.

## OTLP Telemetry (logs + metrics)

Enable OpenTelemetry export from daemon:

```json
{
  "Telemetry": {
    "Enabled": true,
    "Otlp": {
      "Endpoint": "http://127.0.0.1:4317"
    }
  }
}
```

Current instrumentation emits:

- metrics for:
  - events received, dropped, routed
  - messages enqueued
  - replies posted/failed and reply latency histogram

Export target can be LocalTelemetry (or any OTLP collector).

If exporting to Seq through `otlphttp`, use Seq's OTLP ingest path as the
exporter endpoint base:

```yaml
exporters:
  otlphttp/seq:
    endpoint: https://<seq-host>/ingest/otlp
```

Using the root URL (for example `https://<seq-host>/`) causes 404s when the
collector appends `/v1/logs` or `/v1/traces`.

This gives per-turn causality and timing without heavy log spelunking.

You can also inspect the daemon's in-memory Slack flow counters directly:

```bash
netclaw status
```

Look for `slack counters:` and compare `recv/routed/enqueued/replied` to detect
where messages are being dropped.

## OTLP Query Cheat Sheet

Use these metric names in your local telemetry UI (or PromQL-compatible query layer):

- `netclaw.slack.events.received`
  - event ingress count
  - attributes: `kind`
- `netclaw.slack.events.dropped`
  - policy/guard drops (duplicates, ACL, loop prevention, etc.)
  - attributes: `reason`
- `netclaw.slack.events.routed`
  - events that reached conversation routing
  - attributes: `kind`
- `netclaw.slack.messages.enqueued`
  - messages accepted into session input queue
- `netclaw.slack.replies.posted`
  - successful Slack thread replies
- `netclaw.slack.replies.failed`
  - failed Slack reply attempts
- `netclaw.slack.reply.duration.ms`
  - histogram of reply post call duration (ms)

Suggested quick checks:

1. **No reply symptom**
   - `events.received > 0` and `messages.enqueued > 0` but `replies.posted = 0`
   - likely outbound post failure or output mapping issue.

2. **Loop symptom**
   - high `events.dropped{reason="bot_message"}` and high `events.received`
   - indicates self-echo events are arriving and being correctly filtered.

3. **Policy mismatch symptom**
   - spikes in `events.dropped{reason="channel_not_allowed"}` or `user_not_allowed`
   - check Slack ACL config (`AllowedChannelIds`, `AllowedUserIds`, DM settings).

Tracing spans are intentionally disabled for now to avoid disconnected
cross-actor telemetry noise.
