# Project Context - Netclaw

## What Netclaw Is

Netclaw is an **always-on autonomous operations agent** that runs anywhere —
from a Raspberry Pi to a cloud VM. It is a single-process .NET 10 application
built on Akka.Agents, communicating primarily through Slack. It is NOT just a
chat assistant — it is an autonomous operations platform that can monitor,
react, investigate, delegate work, and manage its own schedule.

Netclaw is open source and designed for hobbyists, small teams, and businesses
who want a self-hosted AI operations agent with strong safety defaults.

## Primary Users

- **Hobbyist / self-hoster** — running Netclaw on homelab or personal infrastructure
- **Small team** — shared Slack workspace with team-scoped access controls
- **Business operator** — deploying on managed infrastructure with audit and policy requirements
- Interacts primarily through Slack (including mobile, on the go)
- Needs predictable behavior, persistence, and strong safety defaults
- Wants to pop off background tasks and have them run autonomously

## Full Product Vision

### Core Use Cases

1. **Chat assistant with memory** — message it in Slack, it remembers context
   across restarts, can research topics, delegate work, save findings
2. **Ambient channel monitoring** — listens to alert channels, reacts to
   production issues (pull OTel data from Seq, file GitHub issues)
3. **Delegated coding tasks** — spawns Claude Code / OpenCode in `-p` mode,
   monitors progress, posts PR links back to Slack
4. **Research and scheduling** — web search, browser automation, chat-driven
   scheduled tasks ("check eBay for this GPU every 6 hours")
5. **Chain-reaction workflows** — filing an issue can trigger another session
   to attempt a fix via webhook

### Key Architectural Insight

Everything is just a message arriving at a session actor with context-specific
instructions. The input source (Slack, webhook, timer, future web UI) is
irrelevant — the differentiator is the instructions attached to the context.

| Input Source          | Delivery Mechanism                          |
|-----------------------|---------------------------------------------|
| User @mention         | Slack Socket Mode                           |
| Ambient channel alert | Slack Socket Mode (require_mention: false)  |
| Webhook (GitHub, CI)  | HTTP via Tailscale Serve / Cloudflare Tunnel|
| Scheduled task        | Internal timer                              |
| Web UI (future)       | WebSocket / HTTP                            |

## MVP Outcome

Netclaw answers Slack messages in thread, persists session state across
restarts, compacts long threads, has local memory (project registry,
environment inventory, agent personality), integrates with MCP (Memorizer),
provides basic tool access (web search, shell, GitHub), and supports
chat-driven scheduling.

## Product Boundaries (MVP)

In scope:

- single-process host (gateway + actors in same process)
- Slack Socket Mode adapter
- per-thread session actors keyed by `{channelId}/{threadTs}`
- SQLite journal and snapshot persistence (in-memory for tests)
- compaction via summarization reducer
- pub/sub session broadcasts for adapters and future UI subscribers
- default-deny ACL with explicit channel/sender/data grants
- agent personality / soul (markdown-based system prompt)
- first-party local memory: project registry, environment inventory, config
- MCP server integration (Memorizer for research/knowledge, other tools)
- basic tool access: web search, web fetch, shell execution, GitHub (gh CLI)
- chat-driven scheduling (agent manages its own schedule)
- OpenRouter as primary LLM provider via Microsoft.Extensions.AI (pluggable)
- configurable primary + fallback model
- capability self-discovery (installed tools, credentials, host info)
- self-configuration through conversation (bot updates its own config files)

Out of scope (deferred):

- split gateway/agent processes
- webhook ingress (Tailscale Serve / Cloudflare Tunnel)
- ambient channel monitoring with per-channel instructions
- sub-agent model routing (cheaper models for high-token tasks)
- browser automation
- delegated coding (Claude Code / OpenCode spawning)
- web UI implementation (spec/mockup only in this phase)
- formal approval gates and tool isolation/sandboxing
- telemetry and advanced model capability abstraction layers
- session branching/revert features

## Personality Model

- **Single global personality** for the human-facing chatbot
- **Spawns sub-agents** with specialized system prompts for discrete tasks
  (browser automation, diagnostics, coding)
- **Loads project AGENTS.md** as context overlays when working on registered
  projects (same layered model as Claude Code)

## Memory Architecture

### First-Party Local Memory

- Agent soul / personality (markdown files on disk)
- Project registry: repo paths, alert channels, capabilities, AGENTS.md paths
- Environment inventory: installed tools, credentials, host capabilities
- Scheduled tasks and channel-level instructions
- Storage: files on disk, loaded into agent context at session start
  (alternatively SQLite with vector search if growth warrants it)

### External Memory (MCP)

- Memorizer for research findings, knowledge base, cross-session learning
- Important MCP tool but NOT the core memory mechanism
- Local memory is more personal: file paths, tool availability, project info

## Capability Self-Discovery

Netclaw maintains awareness of its environment:

- Is `claude` / `opencode` CLI available?
- Do I have git credentials? For which hosts?
- What .NET SDK is installed? Can I install new versions via install scripts?
- What repos are registered and where on disk?
- What MCP servers are configured and reachable?

## Autonomy Model

- Fully autonomous for investigation, analysis, filing issues
- Fully autonomous for spawning Claude Code / OpenCode
- **Cannot merge PRs** (enforced by git credential scope)
- Posts progress to Slack threads for passive visibility
- Formal approval gates and tool isolation are post-MVP

## Configuration Model

- Bot can update its own configuration through conversation
- Config stored as files on disk, loaded at session/agent start
- Session reboot needed to refresh config (cached in LLM context)
- Future web UI reads same config files

## LLM Provider Strategy

- OpenRouter as primary provider via `Microsoft.Extensions.AI`
- Configurable primary model + fallback model
- Sub-agent model routing (post-MVP)

## Architectural Decisions to Preserve

- Gall's Law first: simple working system before generalized framework
- Actor transport boundary: adapters consume broadcasts, do not directly drive
  model internals
- Serialization boundary: never persist framework-external chat types directly
- Security boundary: default deny, explicit allow, fail-closed configuration
- Everything is just input: all sources produce messages routed to session
  actors with context-specific instructions

## Phasing

1. **Chat + Memory MVP** — Slack, persistence, compaction, local memory,
   MCP/Memorizer, basic tools, scheduling
2. **Input Expansion** — ambient channels, webhook ingress, channel
   instructions, onboarding wizard
3. **Delegated Coding** — Claude Code / OpenCode spawning, process monitoring
4. **Browser + Research** — web automation, price monitoring, research pipelines
5. **Ops Console** — web UI for config, sessions, diagnostics

## Current Phase

Vision definition and specification. PRDs and engineering specs need revision
to align with the expanded product vision before implementation begins.
