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

4. Check crash evidence immediately when behavior looks like a sudden process
   disappearance:

   ```bash
   ls -lt "$HOME/.netclaw/logs"/crash-*.log
   netclaw doctor
   ```

   `netclaw doctor` includes a daemon crash-log check that flags recent
   `Netclaw daemon*` crash files and points to remediation.

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
- log lines, which include one Info line for each tool call (see below)

Export target can be LocalTelemetry (or any OTLP collector).

### Tool call log line

The daemon writes one Info line for each tool call, before the call runs. A
sub-agent tool call writes the same line. A call that the argument check
rejects also writes the line.

```text
Invoking tool [{ToolName}] (call={CallId}) args={Args} rationale={Rationale} authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId}
```

The exporter sends each field as a separate attribute:

- `ToolName`: the tool name.
- `CallId`: the model's tool call ID.
- `Args`: the call arguments as compact JSON. Meta keys such as `_rationale`
  stay in this field.
- `Rationale`: the `_rationale` text. It is empty when the call has no valid
  rationale.
- `AuthorizationAttemptId`: the same ID as the
  `Tool authorization attempt started` line. Use it to find the authorization
  decision for the call.
- `SessionId`: the session ID. For a sub-agent, this is the parent session ID.

These lines leave the host, so `Args` is redacted and limited. The rules apply
in this order:

1. The daemon examines the argument JSON at every depth.
2. A value under a secret key name (for example `api_key`, `Authorization`, or
   `github_token`) becomes `***REDACTED***`. A `SensitiveString` value also
   becomes `***REDACTED***`.
3. A string value longer than 256 characters becomes
   `<N chars, sha256:XXXXXXXXXXXXXXXX>`. The hex text is the first 16
   characters of the SHA-256 hash of the original value. To match a call to a
   file, compare this hash prefix with `sha256sum` of the file.
4. The secret-pattern redactor runs on the JSON text. It removes known token
   shapes, such as `ghp_...`, JWTs, and `Bearer` headers.
5. The daemon cuts `Args` at 4,096 characters and adds `...`.

Other limits:

- An object or an array deeper than 64 levels becomes `"<nested>"`.
- A JSON string or key that is not valid UTF-16 becomes
  `"<invalid UTF-16 text>"`. The value under such a key becomes
  `***REDACTED***`.
- `Rationale` gets the secret-pattern redactor and a cut at 500 characters.

The line does not hold the tool result. The local `session.log` file holds the
result preview.

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
