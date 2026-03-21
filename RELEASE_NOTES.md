#### 0.7.8 2026-03-21 ####

Netclaw v0.7.8 — CLI packaging fix for installed binaries

**CLI**

* Fixed installed CLI binaries crashing on the `MemoryCheckpointHealth` doctor check — `libe_sqlite3.so` was missing from shipped single-file CLI binaries because `IncludeNativeLibrariesForSelfExtract=true` was set on the daemon publish but not the CLI. The release packaging step now bundles native libs correctly, and the PR validation smoke test now runs against a published binary (matching the exact release artifact) instead of via `dotnet run`. ([#365](https://github.com/Aaronontheweb/netclaw/pull/365))

#### 0.7.7 2026-03-21 ####

Netclaw v0.7.7 — Slack library extraction, lifecycle webhooks, reminder history preservation, and Slack token tracking

**Slack**

* Extracted all Slack-specific infrastructure into a dedicated `Netclaw.Channels.Slack` library — `Netclaw.Channels` is now a thin channel abstractions layer (`IChannel` + `SessionTelemetry`), isolating the SlackNet dependency and establishing the pattern for future channel-specific libraries. Also added a per-target `Format` property to `WebhookTarget` (`"generic"` default, `"slack"` for Block Kit) so the `WebhookNotificationService` renders Slack-compatible payloads with the required `text` field and structured blocks, fixing Slack incoming webhooks. ([#363](https://github.com/Aaronontheweb/netclaw/pull/363))

**Lifecycle**

* Added daemon startup and shutdown webhook notifications — the daemon now posts operational webhooks when it starts and stops, and `netclaw daemon stop` sends a shutdown reason (`"cli-stop"`) to a new `POST /api/lifecycle/shutdown` endpoint before issuing SIGTERM, giving the daemon a chance to fire the webhook before exiting. Shutdown reasons are logged for diagnostics. ([#361](https://github.com/Aaronontheweb/netclaw/pull/361))

**Reminders**

* Fixed one-shot reminder history being lost after execution — the previous auto-delete removed the reminder definition entirely, causing history queries to return 404. One-shot reminders are now soft-deleted after firing so the history API can still return `fired_at`. The list API excludes disabled reminders by default so completed one-shots no longer clutter the visible list. ([#362](https://github.com/Aaronontheweb/netclaw/pull/362))

**Sessions**

* Fixed token usage always showing zero for Slack sessions — `UsageOutput` requires `OutputFilter.Usage`, but Slack channel subscriptions used `Text|Files` only, so the lifecycle observer never saw usage events. Token recording is now performed directly inside the session actor, matching the pattern used by memory and skill recording. ([#354](https://github.com/Aaronontheweb/netclaw/pull/354))

**Evals / Operations**

* Reduced the default per-prompt eval timeout from 180s to 60s — most prompts complete in 15–30s, so the previous timeout wasted minutes on hung calls. Added a daemon health check before each eval case so a mid-run daemon crash aborts immediately with partial results rather than burning through timeouts for all remaining cases. ([#360](https://github.com/Aaronontheweb/netclaw/pull/360))

#### 0.7.6 2026-03-21 ####

Netclaw v0.7.6 — MCP OAuth wiring, session loop hardening, security cleanup, reminder auto-cleanup, and eval suite

**MCP**

* Fixed MCP OAuth connections requiring manual Bearer header injection — the daemon now wires the SDK's built-in OAuth token provider instead of manually inserting the `Authorization` header, eliminating a class of auth failures when tokens refresh mid-session. ([#331](https://github.com/Aaronontheweb/netclaw/pull/331))

**Security**

* Removed the MimeDetective dependency from `MagicByteValidator` — magic byte detection is now handled with a lightweight inline implementation, eliminating a third-party library whose static initializer could poison the validator for the process lifetime. ([#348](https://github.com/Aaronontheweb/netclaw/pull/348))

**Sessions**

* Fixed session loops missing mid-conversation user messages — messages injected while the LLM tool loop was in-flight were previously dropped. The actor now processes these injected messages as follow-up turns. Also added duplicate tool call detection to guard against infinite tool loops where the LLM repeats the same call without making progress. ([#350](https://github.com/Aaronontheweb/netclaw/issues/350), [#351](https://github.com/Aaronontheweb/netclaw/pull/351))

**Reminders**

* Fixed single-shot reminders persisting after firing — one-time reminders were scheduled, delivered, and then left in the store, causing spurious re-deliveries on actor restart. Reminders are now auto-deleted immediately after a one-shot fires. ([#349](https://github.com/Aaronontheweb/netclaw/issues/349), [#353](https://github.com/Aaronontheweb/netclaw/pull/353))

**Identity / Evals**

* Added behavioral grounding rules to the `AGENTS.md` init wizard template and introduced a behavioral eval suite for session pipeline regression testing — operators can now run `./evals/run-evals.sh` to verify that identity grounding, skill loading, memory recall, and tool execution behave correctly end-to-end. ([#347](https://github.com/Aaronontheweb/netclaw/pull/347))

#### 0.7.5 2026-03-21 ####

Netclaw v0.7.5 — Slack image delivery fix and init wizard grounding rules

**Security**

* Fixed Slack images being silently dropped when `MagicByteValidator` type initialization failed — a transient assembly load error during startup could permanently poison the static inspector, causing all image validation to fail for the process lifetime. The scanner failure is now isolated so valid images flow through with a logged warning instead of being discarded. New installs and restarts are no longer affected by one-time startup contention. ([#345](https://github.com/Aaronontheweb/netclaw/pull/345))

**Identity**

* Added behavioral grounding rules to the `AGENTS.md` template generated by `netclaw init` — new installations now start with rules that prevent the agent from stating unverified facts, claiming actions it did not perform, or silently substituting a different answer when the primary task fails. Previously these rules existed only in production deployments that were manually updated. ([#324](https://github.com/Aaronontheweb/netclaw/issues/324), [#344](https://github.com/Aaronontheweb/netclaw/pull/344))

#### 0.7.4 2026-03-20 ####

Netclaw v0.7.4 — Context overflow recovery, skill compaction durability, and frontmatter stripping

**Sessions**

* Fixed sessions failing the turn on context overflow — when the LLM provider rejects a request because the context window is full, the session now triggers emergency compaction and retries instead of surfacing an error. Context overflow detection is hardened across Anthropic, OpenAI, Ollama, and vLLM error formats with 16 new tests. ([#314](https://github.com/Aaronontheweb/netclaw/issues/314), [#340](https://github.com/Aaronontheweb/netclaw/pull/340))

**Skills**

* Fixed auto-loaded skills disappearing after context compaction — skill content injected into the system prompt was not preserved through the compaction pipeline, causing the agent to lose all skill knowledge mid-conversation. Skills are now re-injected after compaction completes. ([#339](https://github.com/Aaronontheweb/netclaw/pull/339))
* Fixed YAML frontmatter leaking into LLM context from auto-loaded skills — skill files include metadata like version numbers in their YAML frontmatter, which the LLM could mistake for its own runtime version. Frontmatter is now stripped before injection so only the skill body reaches the model. ([#324](https://github.com/Aaronontheweb/netclaw/issues/324), [#339](https://github.com/Aaronontheweb/netclaw/pull/339))

#### 0.7.3 2026-03-20 ####

Netclaw v0.7.3 — Skill feed sync fix

**Skills**

* Fixed skill feed sync returning 404s for all resources — the manifest generator leaked CI runner absolute paths (e.g., `/home/runner/_work/...`) into resource file URLs, causing every skill sync to fail. Skills were only available from the built-in seed copy and never updated from the feed. Path normalization now uses `pwd` before prefix stripping, and versioned subdirectories are excluded from the resource list. ([#336](https://github.com/Aaronontheweb/netclaw/pull/336))

#### 0.7.2 2026-03-20 ####

Netclaw v0.7.2 — Session passivation recovery, memory recall activation, skill loading fixes, and per-session log directories

**Stability**

* Fixed sessions going silent after idle passivation — when a session actor passivated due to inactivity and was later re-created, its subscriber list was empty so channels never received output. Channels now re-assert their subscription on each inbound message, recovering transparently. Passivation is also deferred while active subscribers exist. ([#325](https://github.com/Aaronontheweb/netclaw/pull/325))

**Memory**

* Enabled memory recall in production — `MemorySidecarsEnabled` and `DeterministicRetrievalEnabled` were both defaulting to `false` since initial development, which meant `store_memory` wrote to SQLite but nothing read it back automatically. Every session received zero recalled memories on every turn. Both flags now default to `true`. ([#334](https://github.com/Aaronontheweb/netclaw/pull/334))

**Identity**

* Added bot identity grounding to the SOUL.md template — the system prompt now starts with "You are {name}" so the LLM knows its own identity from the first turn instead of confabulating a different persona. Also adds the Netclaw GitHub repo URL to TOOLING.md so the agent can file issues and check releases without web searching. ([#334](https://github.com/Aaronontheweb/netclaw/pull/334))

**Skills**

* Fixed skill auto-loading failures for identity queries — "netclaw" was in the keyword blacklist so queries like "What version of Netclaw?" could not trigger `netclaw-manual`. TF-IDF weighting already handles common tokens, so the blacklist entry was unnecessary. Also fixed a startup race where skills had zero keywords until LLM enrichment completed, and stale keyword cache files are now purged during rescan. ([#333](https://github.com/Aaronontheweb/netclaw/pull/333))

**Diagnostics**

* Moved session logs into per-session directories — logs now live at `{sessionsBase}/{session_id}/logs/` instead of the shared global `~/.netclaw/logs/sessions/` directory. Multiple log files within a session directory clearly show passivation and rehydration cycles. ([#332](https://github.com/Aaronontheweb/netclaw/pull/332))

#### 0.7.1 2026-03-20 ####

Netclaw v0.7.1 — Delivery feedback hardening, token stats, MCP OAuth reconnect, and init race fix

**Stability**

* Fixed Slack delivery failures silently disappearing — transport errors (timeouts, permission denials) now propagate back to the LLM session so the agent can acknowledge the failure on the next turn and operators can see what went wrong. Previously these failures were swallowed and the agent appeared non-responsive. ([#311](https://github.com/Aaronontheweb/netclaw/pull/311))
* Fixed `netclaw init` triggering an uncontrolled daemon restart when run on an existing installation — the daemon is now stopped before config files are written, preventing the file watcher from firing mid-initialization and causing a reconnect loop or lost system prompt. ([#300](https://github.com/Aaronontheweb/netclaw/pull/300))

**CLI**

* Fixed `netclaw stats` reporting zero tokens in/out for OpenAI-compatible providers — the provider now parses the `usage` field from API responses and requests streaming usage data so token counts are accurate. ([#303](https://github.com/Aaronontheweb/netclaw/pull/303))

**MCP**

* Fixed MCP OAuth servers (e.g., Notion) requiring a manual daemon restart after completing OAuth authorization — the daemon now reconnects automatically after a successful token exchange. Fixed `mcp list` to prefer daemon-side statuses for OAuth-protected servers instead of probing directly without credentials. ([#301](https://github.com/Aaronontheweb/netclaw/pull/301))

#### 0.7.0 2026-03-20 ####

Netclaw v0.7.0 — Behavioral directives, webhook alerts, tool budgets, and provider consolidation

**Agents**

* Added deterministic behavioral directives to the system prompt — operators can now place a `DIRECTIVES.md` file in their Netclaw config directory to inject stable, unconditional instructions that are always prepended to the agent's system prompt, independent of skills or personality files. ([#290](https://github.com/Aaronontheweb/netclaw/pull/290))

**Notifications**

* Added operational webhook alerts for daemon events — operators can configure HTTP webhook endpoints to receive structured JSON payloads when key daemon lifecycle events occur (startup, shutdown, error). Useful for integrating Netclaw status into external monitoring and alerting pipelines. ([#292](https://github.com/Aaronontheweb/netclaw/pull/292))

**CLI**

* Added `netclaw stats` command — displays live daemon statistics including uptime, session count, memory usage, reminder count, and provider health in a single glanceable output. ([#272](https://github.com/Aaronontheweb/netclaw/pull/272))
* Unified MCP OAuth behind a shared PKCE flow — the `netclaw mcp auth` command now uses the same PKCE authorization code exchange path for all MCP OAuth providers, eliminating divergent code paths and making headless auth more predictable. ([#293](https://github.com/Aaronontheweb/netclaw/pull/293))

**Session**

* Reworked tool budget to use per-call counting with a budget nudge and graceful wind-down — the agent now tracks individual tool calls rather than turn-level counts, receives a soft warning as it approaches the limit, and transitions to a clean wrap-up phase instead of abruptly stopping. ([#277](https://github.com/Aaronontheweb/netclaw/pull/277), [#280](https://github.com/Aaronontheweb/netclaw/pull/280))

**Reminders**

* Fixed one-shot reminders firing repeatedly after their scheduled time — reminders with `repeat: false` are now disabled immediately after first execution, preventing zombie entries from re-inflating the reminder registry. ([#289](https://github.com/Aaronontheweb/netclaw/pull/289))

**Providers**

* Merged the `openai-codex` provider into the unified OpenAI provider — operators previously using the `openai-codex` provider type will have their configuration automatically migrated. The separate provider type is removed. ([#251](https://github.com/Aaronontheweb/netclaw/pull/251))

**Stability**

* Implemented the delivery feedback contract for LLM error correction — when a channel cannot deliver an LLM response (e.g., Slack formatting rejection), the full error is now fed back into the LLM session so the agent can understand what went wrong and retry with corrected output. Silent failures and fallback downgrades are eliminated. ([#283](https://github.com/Aaronontheweb/netclaw/pull/283))
* Fixed false "fallback" warning logs in Slack and ensured preamble text is surfaced correctly during tool-use turns — preamble content emitted before the first tool call is now delivered to Slack rather than silently dropped. ([#278](https://github.com/Aaronontheweb/netclaw/pull/278))

#### 0.6.2 2026-03-17 ####

Netclaw v0.6.2 — Reminder history, onboarding chat, and turn recovery hardening

**Reminders**

* Added per-reminder execution history: every reminder execution now appends a record (fired time, success/failure, duration, session ID, error message) to `~/.netclaw/reminders/{id}.history.jsonl`. History is capped at 500 records (configurable) with atomic overflow trimming. Accessible via `netclaw reminder history <id> [--last N]`, `GET /api/reminders/{id}/history`, and the `get_reminder_history` agent tool (scheduling grant required). History file is deleted when a reminder is cancelled. ([#259](https://github.com/Aaronontheweb/netclaw/pull/259))

**Init Wizard**

* Added an interactive onboarding chat at the end of the init wizard — after health checks pass, the agent introduces itself and asks what the operator wants to use it for, updating `SOUL.md` with what it learns. This replaces the static "primary use" text field with a richer conversational approach. ([#250](https://github.com/Aaronontheweb/netclaw/pull/250))
* Fixed the chat TUI getting stuck on "Generating..." after an idle SignalR reconnect — `IsGenerating` is now reset on disconnect. ([#250](https://github.com/Aaronontheweb/netclaw/pull/250))

**Providers**

* Fixed OpenAI-compatible provider fallback path using `/api/v1/` instead of the standard `/v1/` — llama.cpp, vLLM, and other servers that follow the OpenAI path convention without the `/api/` prefix now work correctly. ([#253](https://github.com/Aaronontheweb/netclaw/pull/253))

**Skills Platform**

* System skills are now sourced directly from the feed directory (`feeds/skills/.system/files/`) at build time, eliminating the dual-copy maintenance problem where the embedded copy and the feed copy had silently diverged. The feed directory is the single source of truth for both on-disk seeding and published feed artifacts. ([#258](https://github.com/Aaronontheweb/netclaw/pull/258))

**Stability**

* Fixed agent turn recovery for empty LLM responses, forced no-tools turns, and hang scenarios — the session actor now reliably recovers and continues rather than stalling or silently dropping turns in these edge cases. ([#260](https://github.com/Aaronontheweb/netclaw/pull/260), [#269](https://github.com/Aaronontheweb/netclaw/pull/269), [#271](https://github.com/Aaronontheweb/netclaw/pull/271))

#### 0.6.1 2026-03-15 ####

Netclaw v0.6.1 — Init wizard hardening and installer progress indicators

**Init Wizard**

* Removed the Memory backend selection step from the init wizard — SQLite is the only supported backend and no user input is needed. The underlying SQLite health check verification is retained. ([#247](https://github.com/Aaronontheweb/netclaw/pull/247))
* Fixed bracketed paste being silently dropped in the init wizard — `PasteEvent` is now routed to the active text input, matching behavior in the chat and reminder pages. ([#247](https://github.com/Aaronontheweb/netclaw/pull/247))
* Replaced the single-line primary use description field with a multi-line text area so operators can enter longer descriptions without line truncation. ([#247](https://github.com/Aaronontheweb/netclaw/pull/247))
* Added `xoxb-`/`xapp-` prefix validation on Slack token submit — invalid tokens are rejected inline with a status message before the wizard advances. ([#247](https://github.com/Aaronontheweb/netclaw/pull/247))
* Wrapped each async health check stage with per-operation timeouts so the init wizard no longer hangs indefinitely: Slack probe 15s, channel resolution 35s, browser bootstrap 3 minutes, daemon poll 5s per request, overall 5 minutes. ([#247](https://github.com/Aaronontheweb/netclaw/pull/247))

**Installer**

* Added download progress indicators to the install scripts — the Bash installer shows `curl --progress-bar` when stderr is a TTY and falls back to silent mode in non-interactive contexts (CI). The PowerShell installer shows a spinner via a background runspace to avoid the `Write-Progress` performance penalty on PowerShell 5.1. ([#246](https://github.com/Aaronontheweb/netclaw/pull/246))

#### 0.6.0 2026-03-15 ####

Netclaw v0.6.0 — Subagents, deterministic skill loading, and reminder idempotency

**IMPORTANT: This is a breaking change release. All installs prior to 0.6.0 are unsupported and no migration path is provided. Please read the Breaking Changes section before upgrading.**

**Breaking Changes**

* The skills feed has moved to `skills.netclaw.dev` and all skill version numbers have been bumped to 0.6.0. The feed format is incompatible with daemons older than 0.6.0 — older installs will not receive skill updates and may fail to parse the new manifest. There is no plan to support or maintain any Netclaw version prior to 0.6.0.
* Removed `memorizer` and `files` memory backend options. `MemoryConfig.Provider` no longer accepts `memorizer` or `files` values — SQLite is now the only supported memory backend. Configurations referencing either removed option must be updated before upgrading. ([#233](https://github.com/Aaronontheweb/netclaw/pull/233))
* `set_reminder` now requires a caller-provided `Id` as a mandatory parameter. Existing reminder automations or scripts that omit an ID will fail. The `reminder create` CLI command now takes `<id>` as its first positional argument. ([#241](https://github.com/Aaronontheweb/netclaw/pull/241))

**Subagents**

* Added `spawn_agent` tool and `SubAgentDefinitionRegistry` so the frontline agent can delegate tasks to named subagents — research-assistant, code-analyst, and summarizer are seeded during `netclaw init`. Operators can define custom agents by placing `~/.netclaw/agents/<name>.json` and companion `<name>.md` prompt files in that directory. ([#226](https://github.com/Aaronontheweb/netclaw/pull/226))

**Skills Platform**

* Skills now load deterministically before each LLM call based on conversation context — a keyword index (built by a sidecar LLM at scan time and cached per skill version+content hash) scores skills against the current turn. This fixes cases where the agent omitted source URLs in search responses because `search-citation` was never loaded. ([#230](https://github.com/Aaronontheweb/netclaw/pull/230))
* System skills are now published to `skills.netclaw.dev` (Cloudflare R2) with cumulative historical retention — each version of every skill file is preserved at a versioned URL. The manifest gains an `allVersions` array for future version pinning and rollback. Skills publish on release tags and manual dispatch; dev pushes no longer auto-publish. ([#243](https://github.com/Aaronontheweb/netclaw/pull/243))
* System skills renamed to Netclaw-prefixed intent-based IDs (e.g., `netclaw-manual`, `netclaw-memory`, `netclaw-diagnostics`). On-disk skill directories, caches, and references migrate automatically to the new names on next daemon start. ([#239](https://github.com/Aaronontheweb/netclaw/pull/239))
* Fixed search responses in Slack to use inline hyperlinks (`[text](url)`) instead of footnote-style citation markers (`[1]`, `[2]` with a trailing reference list) — the `search-citation` skill now explicitly requires the inline format. ([#235](https://github.com/Aaronontheweb/netclaw/pull/235))

**Reminders**

* `set_reminder` upsert semantics: calling with the same ID now updates the existing reminder instead of creating a duplicate. IDs are normalized to lowercase kebab-case with a 50-character cap. Schedule descriptions in all tool responses, the REST API, and the CLI are now human-readable (e.g., `0 9 * * MON-FRI` → "weekdays at 09:00 UTC"; next-fire times include day-of-week and local timezone). ([#241](https://github.com/Aaronontheweb/netclaw/pull/241))

**Local Model Reliability**

* Context window detection for OpenAI-compatible providers now prefers the live runtime value from the model server (e.g., Lemonade `/props` `n_ctx`) over training metadata from `/v1/models`. The configured `ContextWindow` is validated against the detected provider capacity at startup and clamped to the runtime maximum. ([#238](https://github.com/Aaronontheweb/netclaw/pull/238))
* Fixed image handling for OpenAI-compatible providers: images attached in Slack were silently dropped in multi-turn conversations. Also suppressed raw `<tool_call>` XML leaking into Slack responses and fixed duplicate tool call loops that could repeat 15+ times when assistant history was malformed. ([#227](https://github.com/Aaronontheweb/netclaw/pull/227))

**Provider Diagnostics**

* Provider probe failures now surface the actual API error message extracted from the response body (handles both `{"error": {"message": "..."}}` and `{"error": "..."}` JSON formats) instead of a generic "API key may lack permissions" message. Error wording updated from "API key" to "credentials" to be accurate for OAuth authentication paths. ([#228](https://github.com/Aaronontheweb/netclaw/pull/228), [#224](https://github.com/Aaronontheweb/netclaw/pull/224))

**Stability**

* Fixed a startup crash on minimal Linux systems (containers, VMs) that do not have ICU libraries installed — self-contained `netclawd` and `netclaw` binaries now enable `InvariantGlobalization` and no longer require `libicu` at runtime. ([#223](https://github.com/Aaronontheweb/netclaw/pull/223))

#### 0.5.0 2026-03-13 ####

Netclaw v0.5.0 — Observer-driven memory recall, local model reliability, and skills platform overhaul

**Memory Recall Planning**

* Added sidecar-driven memory observation and recall planning with deterministic policy gates, expiry handling, and SQLite-backed retrieval — the agent now plans what to recall before acting, rather than issuing ad-hoc memory queries. ([#198](https://github.com/Aaronontheweb/netclaw/pull/198))
* Fixed memory curation worker shutdown: `TaskCanceledException` from in-flight store awaitables during daemon stop is now caught and discarded cleanly instead of surfacing as a crash log. ([#190](https://github.com/Aaronontheweb/netclaw/pull/190))

**Local Model Reliability**

* Fixed multi-turn tool calling for OpenAI-compatible providers: assistant messages with tool calls and tool result messages now serialize `tool_calls` and `tool_call_id` correctly — previously the second LLM request in a tool loop contained malformed history causing models to fall back to emitting tool calls as raw text. ([#210](https://github.com/Aaronontheweb/netclaw/pull/210))
* Added text-based tool call extraction for models that emit tool calls as XML-like prose instead of structured fields (observed with Qwen3.5) — the parser activates as a fallback when no structured tool calls are present, making these models usable for multi-step tool workflows. ([#213](https://github.com/Aaronontheweb/netclaw/pull/213))
* Added OpenAI-compatible local endpoint support — operators can now configure endpoints like Ollama or LM Studio as a first-class provider type. ([#208](https://github.com/Aaronontheweb/netclaw/pull/208))
* Fixed capability detection for GGUF and quantized model IDs: the normalizer now strips `.gguf` extensions, GGML quantization suffixes (`-Q5_K_M`, `-IQ2_XXS`, `-Q4_K_XL`), and build-variant segments (`-UD`, `-BPW4`) before capability resolution — models now correctly report multimodal context windows and input types instead of falling back to text-only defaults. ([#215](https://github.com/Aaronontheweb/netclaw/pull/215))

**Skills Platform**

* Adopted the AgentSkills.io SKILL.md standard: skills now use YAML frontmatter (name, description, triggers, license, compatibility, allowed-tools) and a directory-based layout (`skill-name/SKILL.md`) with progressive disclosure via `references/`, `scripts/`, and `assets/` subdirectories. Feed manifest supports per-file SHA-256 verification for multi-file skills. ([#216](https://github.com/Aaronontheweb/netclaw/pull/216))
* Added `netclaw-manual` system skill — the agent's single authoritative reference for all 16 built-in tools across 5 grant categories, CLI commands, scheduling syntax, MCP discovery patterns, and session context. Also updates `netclaw-diagnostics` to v1.2.0 with a cross-reference. ([#217](https://github.com/Aaronontheweb/netclaw/pull/217))
* Added `search-citation` system skill — guides the agent on when to use `web_search` versus training data, requires source URLs for all specific factual claims, and covers local search, travel, and product search verticals with progressive disclosure reference files. ([#219](https://github.com/Aaronontheweb/netclaw/pull/219))

**Session Self-Awareness**

* The agent's session ID (`{channelId}/{threadTs}`) is now injected into the per-turn dynamic context layers, allowing the agent to be self-referential about its own session in tool calls and memory operations. ([#218](https://github.com/Aaronontheweb/netclaw/pull/218))

**Stability**

* Fixed a stream materializer actor leak in the SignalR session binding: stream stage actors were previously created under the global `StreamSupervisor-0` and accumulated over the daemon's lifetime. A new per-session `SignalRSessionActor` scopes the materializer to the session lifecycle, so all stream children are torn down automatically when the session ends. ([#192](https://github.com/Aaronontheweb/netclaw/pull/192))

#### 0.4.0 2026-03-08 ####

Netclaw v0.4.0 — SQLite memory redesign and deterministic memory evals

**Memory Platform Upgrade**

* Replaced file-backed memorizer surfaces with SQLite-backed memory tools and added a policy-first background curation pipeline for checkpoint formation, hygiene, and recall quality. ([#186](https://github.com/Aaronontheweb/netclaw/pull/186))
* Added deterministic memory eval tooling and suite profiles (`smoke` and `realistic`) with query-trace output to make recall regressions diagnosable without LLM variance. ([#186](https://github.com/Aaronontheweb/netclaw/pull/186))
* Added memory health diagnostics and runtime status exposure for checkpoint curation, plus a config-backed switch for disabling system skill sync during local validation. ([#186](https://github.com/Aaronontheweb/netclaw/pull/186))

#### 0.3.6 2026-03-07 ####

Netclaw v0.3.6 — Reminder Slack target UX

**Reminder Target UX**

* Added `--target` support to `reminder create` so operators can specify destinations using human-friendly Slack identifiers (`#channel`, `@user`) or canonical Slack IDs; the daemon resolves these to canonical IDs at schedule time. ([#182](https://github.com/Aaronontheweb/netclaw/pull/182))
* `netclaw reminder` now shows help by default — interactive TUI requires explicit `reminder ui` or `reminder tui` invocation. ([#182](https://github.com/Aaronontheweb/netclaw/pull/182))

#### 0.3.5 2026-03-07 ####

Netclaw v0.3.5 — Reminder Slack notification reliability

**Reminder Notification Delivery**

* Treat reminder notification delivery as part of execution success: `send_slack_message` tool results are now tracked during reminder execution and delivery failures are surfaced as execution failures rather than silently ignored. ([#181](https://github.com/Aaronontheweb/netclaw/pull/181))
* Made tool argument parsing resilient to common LLM key variants (`Message`/`message`, `ChannelId`/`channel_id`) and added `text` as a `Message` alias for `send_slack_message` to prevent delivery failures caused by argument name mismatches. ([#181](https://github.com/Aaronontheweb/netclaw/pull/181))

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
