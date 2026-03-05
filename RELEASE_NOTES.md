#### 0.2.0 2026-03-05 ####

Netclaw v0.2.0 — Scheduled Reminders, Proactive Slack Messaging, and Reliability

**Scheduled Reminders**

* Added a complete reminder subsystem: schedule prompts to execute at a future time (one-shot), on a recurring interval, or via cron expressions — backed by Akka.Reminders for durable scheduling.
* LLM tools: `set_reminder`, `cancel_reminder`, and `list_reminders` — agents can now schedule and manage reminders autonomously.
* CLI commands: `netclaw reminder list` and `netclaw reminder cancel` for operator-side management.
* Reminders post back to the originating Slack thread (self-targeting) or a specified channel target.
* Concurrency limiting, automatic failure-based cancellation, and configurable execution timeouts.

**Proactive Slack Messaging**

* Added `send_slack_message` and `lookup_slack_user` LLM tools so the agent can initiate Slack conversations (DMs and channel threads) proactively without waiting for an inbound message.
* `lookup_slack_user` resolves human-readable names to Slack user IDs at inference time.
* Introduces channel-specific tool registration — tools only appear when their channel adapter is enabled.

**OpenAI OAuth Fix**

* Fixed OpenAI OAuth device flow to use the correct proprietary 4-step protocol instead of the standard RFC 8628 flow, which was returning 403 Forbidden.
* Extracted `IDeviceFlowService` interface and `DeviceFlowServiceFactory` to select the correct implementation per provider, preserving the generic RFC 8628 path for future providers.
* Added friendly error messages for 404 (device code disabled) and network failures.

**Browser Automation Reliability**

* Hardened Playwright MCP init bootstrap and improved browser runtime selection heuristics.
* Added user-space Node bootstrap fallback for browser MCP tooling when system Node is unavailable.
* Fixed sessions upgrade regression on upgraded deployments by adding legacy sessions-table compatibility migration logic and resilient catalog reads for pre-0.1.x schemas.
* Fixed SQLite migration discovery in published single-file daemon binaries by embedding migration SQL assets.
* Changed browser automation onboarding defaults to Playwright MCP; disabled Chrome DevTools selection when no local Chrome executable is detected.
* Improved MCP doctor diagnostics to report explicit browser runtime prerequisites for `browser_chrome_devtools`.

**Slack Reliability and Observability**

* Hardened Slack session recovery to handle connection drops and reconnect sequences more gracefully.
* Fixed image media persistence so attachments sent via Slack survive session restarts.
* Added live Slack message counters to the status display.
* Hardened sidecar timeout observability with more granular timeout reporting.

**Cross-session File Handoff**

* `attach_file` can now import files from sibling Netclaw session directories (`.../sessions/*`, `.../netclaw-sessions/*`) by copying them into the current session's `attachments/` folder — resolving repeated tool failures during screenshot handoffs between sessions.
* Strict default-deny behavior preserved for arbitrary filesystem paths; symlink escapes rejected.

**Code Quality**

* Added Roslyn analyzer baseline and enforced cancellation token forwarding across async call chains.

#### 0.1.3 2026-03-04 ####

Netclaw v0.1.3 — OpenAI OAuth, CLI, Diagnostics, and Reliability Fixes

* Added OpenAI OAuth device flow authentication (RFC 8628) — operators can now authenticate with OpenAI interactively via browser instead of managing API keys manually. Available in the Provider Manager TUI, Init Wizard, and via `netclaw provider add <name> openai --auth oauth-device` from the CLI. OAuth tokens are stored encrypted at rest.
* Added `netclaw --version`, `netclaw version`, and `netclaw -V` commands to print the CLI binary version without a running daemon.
* Fixed false-positive doctor errors caused by stale crash logs when the daemon has since been restarted — `SqliteProvisioningDoctorCheck` now compares crash log timestamps against the PID file before reporting an error.
* Fixed session title generation failures with thinking models (e.g. `qwen3.5:9b`) by increasing the generation timeout from 10s to 30s and routing failures through the daemon log instead of silently discarding them. ([#84](https://github.com/Aaronontheweb/netclaw/issues/84))
* Fixed auto-update checks producing false "up to date" results by pointing the binary update manifest at `https://releases.netclaw.dev/manifest.json`.

#### 0.1.2 2026-03-04 ####

Netclaw v0.1.2 — Daemon Startup and Diagnostics Hardening

* Fixed Linux single-file daemon SQLite startup failures by enabling native library self-extract for `netclawd` publishes.
* Added a dedicated `SQLite Provisioning` doctor check to surface daemon crash-log evidence when SQLite initialization fails.
* Improved Slack doctor token handling to decrypt encrypted secrets before probe and report corrupted encrypted token errors clearly.
* Added regression coverage for SQLite provisioning diagnostics and encrypted Slack token failure handling.

#### 0.1.1 2026-03-04 ####

Netclaw v0.1.1 — Release Feed and Installer Fixes

* Removed NuGet package publishing from the release workflow and renamed it to focus on binary release artifacts.
* Fixed release pipeline R2 upload command sequencing and stabilized reruns.
* Updated Linux and Windows install scripts to read from `https://releases.netclaw.dev/manifest.json`.
* Updated README install commands to use `releases.netclaw.dev`.
* Published release feed artifacts (manifest, minisign signature, public key, install scripts) directly to the releases host.

#### 0.1.0 2026-03-04 ####

Netclaw v0.1.0 — Initial Release

Netclaw is an always-on autonomous operations agent for homelab infrastructure.
It runs as a single .NET 10 process, communicates through Slack, persists
session state across restarts, and executes tasks autonomously on your behalf.

**Core Agent & Session**

* Actor-based session engine: each Slack thread (`{channelId}/{threadTs}`)
  is an isolated, persistent session actor backed by SQLite journal and
  snapshot storage
* Tiered context compaction with extractive reducer — long threads are
  compacted without losing conversation context
* Adaptive compaction, context overflow detection, and session observability
* Session resume: conversation history is replayed on reconnect
* Session browsing and resume support in the TUI
* Max tool iterations circuit breaker and parallel tool execution
* Fixed agent stalls caused by empty post-tool LLM responses

**Slack Integration**

* Slack Socket Mode adapter with layered channel actor hierarchy
* Slack DM support with `MentionRequiredInDm` option and auto-enable when
  user IDs are configured
* Slack file flow (attach and receive files in conversation)
* Config wizard integration for Slack auth and channel name resolution
* Auto-link bare URLs in Slack Block Converter output
* Secure Slack defaults with default-deny ACL

**LLM Provider Support**

* Multi-provider configuration system via `Netclaw.Configuration`
* OpenRouter as primary provider via `Microsoft.Extensions.AI`
* Provider plugin architecture with resilience pipeline
* CLI model selection and provider management
* Ollama support with `OllamaCapabilityResolver` for provider-first
  capability detection
* Startup capability auto-detection for multimodal models
* Model capabilities surfaced in status endpoint

**MCP Tool Integration**

* MCP tool integration with dynamic discovery and per-turn reset
* MCP OAuth 2.1 Authorization Code + PKCE authentication
* MCP connectivity checks, schema sanitization, and headless UX improvements
* File-backed progressive MCP tool discovery
* Browser MCP onboarding and context safeguards

**Built-in Tools**

* Web search and web fetch with session-scoped file output
* Multimodal image pipeline with modality gating and file output
* Headless prompt (`-p`) mode for scripted/non-interactive use

**Memory & Context**

* File-based memory fallback (unified memory provider M1)
* SubAgentActor with Memorizer MCP-backed memory tools
* Unified context discovery: skills, memory, and observational context layers
* Identity file system (SOUL, AGENTS.md, environment inventory) loaded as
  context layers at session start

**Skills System**

* System skills feed infrastructure with signed manifest and binary
  distribution
* Skill triggers metadata and execution stance for agent autonomy control
* Built-in skill nudges for proactive memory and diagnostics

**Binary Distribution & Auto-Update**

* Binary distribution feed with signed release manifest
* Auto-update system: agent checks for and applies new releases

**Configuration & Init Wizard**

* Procedural init wizard covering provider config, model selection, Slack
  auth, and DM configuration
* Search provider abstraction with init wizard integration
* Daemon restart on config file change (no manual restart required)
* `appsettings.Local.json` support for machine-specific overrides

**CLI & TUI**

* TUI with multi-line text input, paste debounce, status bar, crash logging,
  and tool call timers
* Daemon + thin client architecture over SignalR with streaming and typed
  session recovery
* CLI model selection, runtime env var customization, and provider management
* Detailed status endpoint exposing build version, commit hash, build
  timestamp, and uptime (corrected to reflect soft restarts)
* Doctor command with autofix flow and domain-split diagnostics
* Fixed README Quick Start to reflect actual setup steps

**Observability & Telemetry**

* OTLP logs and metrics with typed daemon telemetry configuration
* Session logging: user prompts, richer transcripts, and sortable filenames
* Per-session disk logging with console framework noise suppression

**Security**

* Secrets protection: encryption at rest using `Microsoft.AspNetCore.DataProtection`
* Tool deny-list to block access to protected Netclaw paths
* Hardened accidental secret leak guardrails across shell tool output
* `ToolPathPolicy` command validation with Windows compatibility
* Default-deny ACL with explicit channel/sender/data grants

**Stability Fixes**

* Fixed daemon CWD anchored to user-owned temp directory at startup
  (resolves permission issues on Linux/macOS)
* Fixed `DaemonClient` reconnect race condition
* Fixed `JoinSession` race condition with deterministic `Ask<SessionJoined>` pattern
* Fixed OpenRouter streaming and provider endpoint resolution
* Fixed compaction losing conversation context

**Legal**

* Project re-licensed to AGPLv3 with Commons Clause restriction
