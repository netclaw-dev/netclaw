# Agent Gateway Architecture Research

**Date:** 2026-02-22
**Context:** Architectural research before designing Netclaw's channel abstraction

## Projects Studied

| Project | Language | Stars | Architecture |
|---------|----------|-------|-------------|
| OpenClaw | TypeScript | 218k+ | Gateway + Agent in one Node.js process |
| IronClaw | Rust | 3k | Hub-and-spoke, Axum gateway + Agent loop as Tokio tasks |
| ZeroClaw | Rust | 17k | daemon command runs gateway + channels + agent + scheduler as Tokio tasks |

## Consensus: Single Process, Logical Separation

All three are single-process architectures. None split gateway and agent into
separate processes for normal operation.

### Gateway vs Agent Boundary

All three have a clean logical boundary but no process boundary:

- **OpenClaw**: Gateway is a WebSocket control plane (`ws://127.0.0.1:18789`),
  Agent is an embedded "peer" in the same process. External agents can connect
  via ACP (stdin/stdout NDJSON protocol), but the default agent is in-process.
- **IronClaw**: Gateway is an Axum HTTP server implementing the `Channel` trait.
  Agent loop processes messages from all channels via a merged async stream.
  Docker containers are the only separate processes (for sandboxed tool
  execution).
- **ZeroClaw**: Same pattern — gateway, channels, agent, scheduler all
  supervised as Tokio tasks in one process. Optimized for $10 hardware
  (Raspberry Pi Zero).

### Security Perimeter

All three put the security boundary at the gateway/channel layer, not between
gateway and agent:

- **OpenClaw**: DM pairing (default-deny, approve unknown senders), role-based
  access (operator/node), tool policy (glob allow/deny), exec approval gates,
  optional Docker sandbox.
- **IronClaw**: Device pairing (one-time code → bearer token), WASM sandbox for
  tools, Docker sandbox for shell, safety layer (prompt injection defense, secret
  leak scanning), tool domain separation (Orchestrator vs Container).
- **ZeroClaw**: Device pairing, three autonomy levels (ReadOnly/Supervised/Full),
  command allowlists, forbidden path blocklists, rate limits, approval workflows,
  env sanitization on every shell exec.

### CLI/TUI as a Channel

In all three, the CLI/TUI is just another channel — it implements the same
interface as Slack, Discord, etc. and feeds messages into the same pipeline.

- OpenClaw: `openclaw tui` connects to the Gateway WebSocket
- IronClaw: `CliChannel` implements the `Channel` trait, feeds `ChannelMessage`
  into same `mpsc::Sender` as all channels
- ZeroClaw: `CliChannel` feeds into shared `mpsc::Sender<ChannelMessage>`

### Tool Execution: Layered Security

All three use layered tool security:

1. **Policy/allowlist filtering** — which tools the agent can see
2. **Runtime approval gates** — user confirms before dangerous operations
3. **Sandbox isolation** — Docker/WASM for shell execution
4. **Output sanitization** — secret scrubbing before feeding back to LLM

### Channel Trait Patterns

**IronClaw** (`Channel` trait in Rust):
```
- name() -> &str
- send(message) -> Result
- listen(tx: mpsc::Sender<ChannelMessage>) -> Result
- health_check() -> bool
- start_typing(recipient) / stop_typing(recipient)
- supports_draft_updates() -> bool
- send_draft() / update_draft() / finalize_draft()
- add_reaction()
```

**ZeroClaw** (`Channel` trait in Rust):
```
- Same push-based pattern: listen() receives an mpsc::Sender
- All channels feed ChannelMessage structs into a shared sender
- daemon supervises all configured channels as concurrent Tokio tasks
```

**OpenClaw** (Node.js channel plugins):
```
- Each channel is a module that normalizes to common message format
- Session identity: {channel}:{kind}:{id}
- Multi-agent routing: different channels can route to different workspaces
```

### Message Identity Patterns

| Project | Session Key Format |
|---------|-------------------|
| OpenClaw | `{channel}:{kind}:{id}` (e.g., `slack:dm:U12345`) |
| IronClaw | `thread_ts: Option<String>` on IncomingMessage |
| ZeroClaw | `thread_ts: Option<String>` on ChannelMessage |
| Netclaw (current) | `{channelId}/{threadTs}` |

### Multi-Process Extensions (Not Default)

While all default to single-process, each has escape hatches:

- **OpenClaw**: ACP bridge (stdin/stdout NDJSON) for external agent processes;
  Nodes (macOS/iOS/Android companion apps) connect as WebSocket clients
- **IronClaw**: Docker containers for sandboxed tool execution; worker mode
  (`ironclaw worker`) for container instances that connect back to orchestrator
  via HTTP; Claude Code bridge mode for delegated coding
- **ZeroClaw**: Docker runtime for tool execution isolation; WASM runtime module
  for serverless/edge deployment

## Key Decisions for Netclaw

1. **Stay single-process** — all three validate this for homelab/personal use
2. **TUI goes through the same pipeline as Slack** — just another channel
3. **Security boundary at the channel layer** — pairing/auth before messages
   enter, tool policy inside
4. **Channel abstraction is the key interface** — Slack, TUI, HTTP, timers all
   implement the same contract
5. **Layered tool security** — policy filtering → approval gates → sandbox
6. **Session identity from the channel** — channel provides the entity key
