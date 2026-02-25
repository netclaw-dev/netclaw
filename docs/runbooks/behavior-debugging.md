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

## Optional OTEL Workstream (Recommended)

If debugging latency/routing frequently, add OpenTelemetry instrumentation:

- activity source spans for:
  - Slack ingress event receive
  - conversation routing decision
  - session enqueue
  - model/tool turn completion
  - Slack reply post
- span attributes:
  - `session.id`
  - `slack.channel_id`
  - `slack.thread_ts`
  - `slack.event_id`
- OTLP export to local collector (e.g. LocalTelemetry)

This gives per-turn causality and timing without heavy log spelunking.
