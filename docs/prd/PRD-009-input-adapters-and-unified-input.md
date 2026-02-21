# PRD-009: Input Adapters and Unified Input Architecture

## Status

- State: Draft for execution (new, mostly post-MVP)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Depends on: `PRD-001`, `PRD-002`, `PRD-008`

## Goal

Define the unified input architecture that treats all message sources
identically. While MVP implements only Slack Socket Mode and internal timers,
the architecture must support future input sources without structural changes.

## Key Architectural Insight

Everything is just a message arriving at a session actor with context-specific
instructions. The input source is irrelevant — the differentiator is the
instructions attached to the context.

All inputs produce a `SendUserMessage` command routed to the session parent
actor. The session parent extracts an entity key and routes to the correct
child session actor.

## Input Source Taxonomy

| Input Source          | Delivery Mechanism                          | Phase | Entity Key Pattern |
|-----------------------|---------------------------------------------|-------|--------------------|
| Local TUI             | `netclaw chat` in-process                   | 1     | `tui/{sessionId}` |
| User @mention         | Slack Socket Mode                           | 1     | `{channelId}/{threadTs}` |
| Scheduled task        | Internal Akka timer                         | 1     | `schedule/{taskId}/{runTs}` |
| Ambient channel alert | Slack Socket Mode (require_mention: false)  | 2     | `{channelId}/{threadTs}` |
| Webhook (GitHub, CI)  | HTTP via Tailscale Serve / Cloudflare Tunnel| 2     | `webhook/{source}/{eventId}` |
| Web UI (future)       | WebSocket / HTTP                            | 5     | `web/{sessionId}` |

### MVP Input Adapters

**Local TUI Adapter** (Phase 1):
- Hosted in-process by `netclaw chat` command
- Receives keyboard input via Termina TextInputNode
- Produces `SendUserMessage` commands with entity key `tui/{sessionId}`
- Subscribes to session broadcasts for reply delivery
- Renders responses as streaming text via StreamingTextNode
- Displays tool invocation status inline (name, duration, spinner)
- Shows MCP server connectivity in status bar
- Full actor system runs in-process — validates entire agent stack locally

**Slack Socket Mode Adapter** (Phase 1):
- Connects via Slack's WebSocket-based Socket Mode
- Receives `message` and `app_mention` events
- Extracts entity key: `{channelId}/{threadTs}`
- Converts Slack event to `SendUserMessage` command
- Subscribes to session broadcasts for reply delivery
- Posts reply messages back to originating Slack thread

**Internal Timer Adapter** (Phase 1):
- Fires on Akka timer ticks for scheduled tasks
- Creates `SendUserMessage` with task instruction as content
- Uses entity key: `schedule/{taskId}/{runTs}`
- Result broadcast is consumed by Slack adapter for delivery

### Post-MVP Input Adapters

**Ambient Channel Monitor** (Phase 2):
- Same Slack Socket Mode connection, different routing
- Channels configured with `require_mention: false`
- Messages are filtered by channel-level instructions before routing
- Channel instructions define what the agent should watch for

**Webhook Adapter** (Phase 2):
- HTTP endpoint behind Tailscale Serve or Cloudflare Tunnel
- Receives POST from GitHub, CI/CD, or other external systems
- Webhook definitions specify: source, expected payload, routing rules
- Each webhook hit creates a new session with source-specific instructions

**Web UI Adapter** (Phase 5):
- WebSocket connection from Blazor Server ops console
- Provides interactive session control and real-time updates
- Same pub/sub broadcast pattern as Slack adapter

## Adapter Contract

All adapters implement the same pattern:

1. **Receive** external input (Slack event, timer tick, HTTP POST)
2. **Transform** into `SendUserMessage` command with:
   - Message content (user text, webhook payload, task instruction)
   - Entity key (determines which session actor handles it)
   - Source metadata (adapter type, sender identity, channel)
   - Context instructions (channel-level rules, webhook routing)
3. **Route** command to session parent actor
4. **Subscribe** to session broadcast for response delivery
5. **Deliver** response back through the originating channel

The session actor never knows or cares which adapter sent the message. This is
the transport-agnostic boundary defined in `PROJECT_CONTEXT.md`.

## Channel Instructions (Phase 2)

Per-channel configuration that shapes how the agent processes messages from
specific channels:

```json
{
  "channel": "C0123ALERTS",
  "require_mention": false,
  "instructions": "This is an alert channel. When you see error messages, check Seq for related logs and file a GitHub issue if the error is new.",
  "tool_grants": ["web_fetch", "github", "shell"],
  "auto_respond": true
}
```

Channel instructions are loaded as context overlays (same layered model as
project AGENTS.md files).

## Non-Goals (MVP)

- Ambient channel monitoring implementation
- Webhook ingress implementation
- Web UI adapter implementation
- Channel-level instruction configuration
- Dynamic adapter registration/hot-plug
- Multi-platform adapters (Discord, Telegram, etc.)

## MVP Requirements

### INPUT-001 Slack Adapter

Slack Socket Mode adapter SHALL receive events, extract entity keys, produce
`SendUserMessage` commands, subscribe to broadcasts, and deliver replies.

### INPUT-002 Timer Adapter

Internal timer adapter SHALL fire on schedule, create fresh sessions for task
execution, and route results to Slack delivery.

### INPUT-003 Transport Agnosticism

Session actors SHALL never reference adapter-specific types. The
`SendUserMessage` command and broadcast events are the only contract between
adapters and session actors.

### INPUT-004 Source Metadata

All inbound commands SHALL carry source metadata sufficient for ACL evaluation
and audit logging: adapter type, sender identity, channel, timestamp.

### INPUT-005 TUI Adapter

The TUI adapter SHALL receive keyboard input via Termina TextInputNode, produce
`SendUserMessage` commands with entity key `tui/{sessionId}`, subscribe to
session broadcasts, and render responses as streaming text. The TUI adapter
SHALL display tool invocation status inline between user message and response.

## Acceptance Criteria (MVP)

1. TUI adapter receives input, routes through session actor, renders streaming
   response.
2. Slack adapter handles @mention events and replies in thread.
3. Timer adapter fires scheduled tasks and delivers results via Slack.
4. Session actors receive identical command types regardless of source.
5. Source metadata is available for ACL checks and audit logging.

## Acceptance Criteria (Phase 2)

5. Ambient channel adapter processes messages without @mention.
6. Webhook adapter accepts POST and creates routed sessions.
7. Channel instructions shape agent behavior per channel.

## Cross-References

- Session architecture: PRD-001 (FR-001, FR-002, FR-008)
- Security (ACL per source): PRD-002
- Scheduling (timer source): PRD-008
- Ops console (web UI source): PRD-003
