#### 0.3.4 2026-03-07 ####

Netclaw v0.3.4 — OAuth device flow sequencing fix

**OpenAI OAuth Reliability**

* Fixed OAuth device flow sequencing so success is only published after token assignment, preventing probe validation from running before credentials are available and avoiding false "API key or OAuth token is required" failures. ([#178](https://github.com/Aaronontheweb/netclaw/pull/178))

#### 0.3.3 2026-03-07 ####

Netclaw v0.3.3 — OpenAI setup reliability and session traceability

**OpenAI Setup Reliability**

* Fixed OAuth probing in the init wizard by reusing the OAuth access token when API key input is empty, preventing false "missing credentials" probe failures after successful device auth.
* Added a manual model-entry continuation path on provider validation failure (`M`) so setup can proceed when model discovery fails due to permissions or endpoint behavior. ([#176](https://github.com/Aaronontheweb/netclaw/pull/176))

**Session and Slack Telemetry**

* Added turn-level correlation metadata (`TurnId`, `MessageId`) across Slack/SignalR ingress, session pipeline, and session actor logs for end-to-end tracing.
* Added structured session lifecycle events for turn receive, LLM call start, tool iteration limit, and turn failure to make "amnesia" and reply continuity issues diagnosable from logs. ([#176](https://github.com/Aaronontheweb/netclaw/pull/176))

**Validation UX**

* Fixed probe state publication ordering so validation screens no longer get stuck showing a spinner after probe completion. ([#174](https://github.com/Aaronontheweb/netclaw/pull/174))

#### 0.3.2 2026-03-06 ####

Netclaw v0.3.2 — Session catalog hardening, diagnostics, and CLI improvements

**Session Catalog Hardening**

* Auto-created missing `sessions` table instead of silently returning empty results; first access triggers table creation or migration from legacy schema (`session_id` column).
* Migrated legacy sessions table to current schema via atomic SQLite transaction with consistent `CREATE TABLE` DDL on both modern and legacy paths.
* `ListRecent()` now logs a warning and returns an empty list only when schema init/migration itself fails, not on schema mismatch. ([#162](https://github.com/Aaronontheweb/netclaw/issues/162))

**SignalR Session Stall Fix**

* Fixed session stall after idle passivation — `SessionRegistry` now detects stale output streams and re-materializes the Akka.Streams pipeline to reconnect subscribers.
* Added correlation logging for session attach, detach, and disconnect events with connection ID and session ID for post-mortem tracing. ([#163](https://github.com/Aaronontheweb/netclaw/issues/163))

**Error Correlation IDs in Slack**

* Added 8-character hex ref (correlation ID) to Slack error fallback messages for cross-referencing with daemon logs.
* Categorized errors at call sites: `ToolFailure`, `ProviderFailure`, `StreamFailure`, `Timeout`, and `Unknown`. ([#164](https://github.com/Aaronontheweb/netclaw/issues/164))

**Reminder Execution Diagnostics**

* Added structured lifecycle logging for reminder execution (Dispatched, Initialized, Completed, Failed, Timeout) with execution IDs, reminder IDs, and full exception chains.
* Added `/status` endpoint displays reminder health: scheduled count, active executions, and failure tracking count. ([#165](https://github.com/Aaronontheweb/netclaw/issues/165))

**CLI Improvements**

* **Bare invocation**: `netclaw` with no args now prints help and exits 2 (was launching chat TUI).
* **Unknown commands**: Invalid subcommands print an error message, show help, and exit 2.
* **--once mode for sessions**: `netclaw sessions --once` lists recent sessions as plain text or JSON without launching TUI; `netclaw status` and `netclaw chat -p` return correct exit codes (0=success, 1=error). ([#166](https://github.com/Aaronontheweb/netclaw/issues/166), [#167](https://github.com/Aaronontheweb/netclaw/issues/167))

**Update Availability in Status**

* Added update check to `netclaw status`: always shows `update:` line with state — `up-to-date`, `UPDATE AVAILABLE`, or `unknown` (timeout/network failure).
* Concurrent update fetch with 3s timeout; release notes URL propagated to JSON output. ([#168](https://github.com/Aaronontheweb/netclaw/issues/168))

**Search Backend Fixes**

* Fixed Brave Search gzip decompression: now validates `Content-Encoding` and `Content-Type`, auto-decompresses gzip responses, and returns structured error for unsupported encodings. ([#161](https://github.com/Aaronontheweb/netclaw/issues/161))

#### 0.3.1 2026-03-06 ####

Netclaw v0.3.1 - Provider probe resilience and diagnostics

**Provider Probe Reliability**

* Hardened provider probe flows in the init wizard, provider manager, and model manager with explicit timeout and exception handling so validation failures no longer hang indefinitely. ([#158](https://github.com/Aaronontheweb/netclaw/pull/158))

**Diagnostics**

* Added provider probe diagnostics logging to `~/.netclaw/logs/provider-probe.log` with probe ID, source, endpoint host, elapsed time, and failure details to make OAuth and model discovery failures easier to diagnose. ([#158](https://github.com/Aaronontheweb/netclaw/pull/158))

#### 0.3.0 2026-03-05 ####

Netclaw v0.3.0 — Slack allowlist persistence and Playwright MCP isolation

**Slack Init Wizard Reliability**

* Fixed Slack allowlist persistence in the init wizard so saved allowlists survive restarts and reconfigure flows. (#154)

**Playwright MCP Isolation**

* Isolated Playwright MCP sessions per tool context to prevent cross-tool leakage and improve reliability when multiple tools run in parallel. (#153)

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
