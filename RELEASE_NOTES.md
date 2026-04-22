#### 0.14.3 2026-04-22 ####

Netclaw v0.14.3 — Webhooks CLI, scheduling audience gating, proactive check-back guidance, and security/reliability fixes

**Features**

* Added `netclaw webhooks` CLI command group for managing inbound webhook routes — `webhooks list`, `webhooks show`, `webhooks set`, `webhooks delete`, and `webhooks validate` commands provide full CRUD management of webhook route configuration from the terminal. Supports multiple secret input methods (`--secret`, `--secret-file`, `--secret-env`) to avoid shell history exposure, `--dry-run` preview mode, and `--create-only` / `--update-only` flags for explicit upsert control. Closes [#529](https://github.com/Aaronontheweb/netclaw/issues/529). ([#711](https://github.com/Aaronontheweb/netclaw/pull/711))

* Added scheduling audience gating — `set_reminder`, `list_reminders`, `cancel_reminder`, and `get_reminder_history` now respect the session's audience `AllowedTools` list. Public and Team audiences no longer have scheduling access by default; only Personal sessions (with `ToolsMode=All`) retain it. Operators can grant Team access by explicitly adding the tool names to `AllowedTools`. Closes [#710](https://github.com/Aaronontheweb/netclaw/issues/710). ([#714](https://github.com/Aaronontheweb/netclaw/pull/714))

* Added proactive check-back guidance to `AGENTS.md` template — the agent now automatically schedules `current_session` reminders when it kicks off async work (builds, CI pipelines, deployments) instead of waiting for the user to ask for status. New installations pick this up via the init wizard; existing users can copy the "Proactive Check-Back" block into `~/.netclaw/identity/AGENTS.md` manually. ([#714](https://github.com/Aaronontheweb/netclaw/pull/714))

**Bug Fixes**

* Fixed false `StreamIdleTimeout` when `ToolCallTextFilter` suppresses SSE events — when the filter detects `<tool_call>` XML in streaming text it suppresses subsequent SSE updates, creating a watchdog blackout where `ProcessingWatchdog.Refresh()` is never called and the 120-second idle timeout fires even while the GPU is actively generating tokens. The fix yields a content-free keepalive `ChatResponseUpdate` when text is suppressed so the watchdog resets unconditionally. Fixes [#717](https://github.com/Aaronontheweb/netclaw/issues/717). ([#720](https://github.com/Aaronontheweb/netclaw/pull/720))

* Fixed skill index missing descriptions, causing skill auto-loading failures — the skill discovery index only showed file paths without context about when to load each skill, leaving the model unable to decide which skills were relevant. `GenerateIndex()` now includes skill descriptions on each line, and AGENTS.md skill reference guidance uses action-oriented "BEFORE you..." language to improve auto-loading accuracy. Fixes [#696](https://github.com/Aaronontheweb/netclaw/issues/696). ([#712](https://github.com/Aaronontheweb/netclaw/pull/712))

* Fixed MIME type rejection for markdown files sent from Slack — Slack reports `.md` files with MIME type `text/plain` instead of `text/markdown`, causing the content scanner to reject them. Extension-based MIME normalization now corrects known mismatches (`.md`/`.markdown`, `.json`, `.yaml`/`.yml`, `.csv`, `.xml`) before validation. Fixes [#716](https://github.com/Aaronontheweb/netclaw/issues/716). ([#719](https://github.com/Aaronontheweb/netclaw/pull/719))

* Fixed `*unsaved*` indicator visibility in `netclaw mcp permissions` TUI — the status message is now positioned above the tool list so it remains visible regardless of list length, and the unsaved indicator color is changed from gray to yellow for better contrast. ([#709](https://github.com/Aaronontheweb/netclaw/pull/709))

#### 0.14.2 2026-04-21 ####

Netclaw v0.14.2 — Structured reminder delivery, protobuf serialization, subagent robustness, and web-content-retrieval skill

**Features**

* Added structured reminder delivery contract — `set_reminder` now uses a `delivery` object with a required `kind` field (`current_session`, `channel`, or `none`) that directly selects the execution mode, replacing the previous implicit inference from optional fields. A new `deliveryRequired` boolean controls policy; `deliveryInstructions` carries content guidance only. Transport-keyed resolver dispatch uses `ChannelType.ToWireValue()` for reliable routing across Slack and future transports. Closes [#690](https://github.com/Aaronontheweb/netclaw/issues/690). ([#692](https://github.com/Aaronontheweb/netclaw/pull/692))

* Added protobuf-net serializer with stable manifests — `NetclawProtobufSerializer` uses constant manifest strings (`sid-v1`, `sum-v1`, `tr-v1`, etc.) decoupled from .NET type names, enabling safe schema evolution without migration steps. The `WithNetclawSerialization()` extension disables the `System.Object` JSON fallback so unregistered types fail loudly instead of silently falling back. Existing persisted events remain readable; new events use the more efficient binary format. ([#705](https://github.com/Aaronontheweb/netclaw/pull/705))

* Added `web-content-retrieval` system skill — the agent now loads a built-in skill covering URL handling, browser automation guidance, and social media content retrieval so it can advise on web fetch workflows without requiring a custom skill. ([#702](https://github.com/Aaronontheweb/netclaw/pull/702))

* Made subagent tools optional — omitting the `tools` field in a subagent's YAML frontmatter now causes the subagent to inherit all session tools, including MCP tools (Notion, GitHub, etc.). Previously the field was required and restricted to four built-in tools, making MCP-powered subagents impossible to define. When `tools` is specified it acts as a whitelist; when omitted all session tools are available. ([#703](https://github.com/Aaronontheweb/netclaw/pull/703))

* Migrated webhook notification policy to `deliveryRequired` boolean — the `NotifyPolicy` enum on `set_webhook` and webhook config is replaced by a `deliveryRequired` boolean that is consistent with the reminder delivery contract. ([#704](https://github.com/Aaronontheweb/netclaw/pull/704))

**Bug Fixes**

* Fixed daemon crash on subagent timeout — the subagent cancellation callback was capturing `Self` at registration time instead of before registration, causing a `NotSupportedException` when the callback fired on a thread-pool thread with no active actor context. The resulting unhandled `AggregateException` terminated the entire daemon, killing all active sessions. The fix also converts the scheduling to the `IWithTimers` pattern per project conventions. ([#707](https://github.com/Aaronontheweb/netclaw/pull/707))

* Fixed `netclaw-operations` skill hidden from agent skill index — the skill had an incorrect `disable-model-invocation: true` flag that prevented it from appearing in the agent's skill discovery index, causing the agent to be unaware of operations guidance. ([#702](https://github.com/Aaronontheweb/netclaw/pull/702))

#### 0.14.0 2026-04-15 ####

Netclaw v0.14.0 — Skill-defined subagent routing, Mode B reminder session re-entry, fail-closed MCP add, and security patch

**Features**

* Added `metadata.subagent` routing for skill-defined activations — skills can now declare a `subagent` field in their YAML frontmatter to route activations to a named subagent instead of executing inline. The router fails loudly when the target subagent is missing or misconfigured, matching the project's no-silent-fallback policy. A new `subagent-authoring` system skill documents the frontmatter contract and guides operators through defining file-based subagents. ([#672](https://github.com/Aaronontheweb/netclaw/pull/672))

* Added Mode B reminder session re-entry — reminders fired from Slack or SignalR sessions can now check back into the originating session when they fire instead of requiring a `report_to_channel` target. Omitting `report_to_channel` from `set_reminder` enables session check-back; the reminder re-enters the session as if the user sent a follow-up message. Fixes [#660](https://github.com/Aaronontheweb/netclaw/issues/660). ([#670](https://github.com/Aaronontheweb/netclaw/pull/670))

* Added fail-closed `mcp add`, server-default approval policy, and `netclaw mcp permissions` TUI — `netclaw mcp add` now assigns empty grants and an `Approval` default to every newly registered server, preventing new tools from executing without explicit operator authorization. The new `netclaw mcp permissions` command provides a terminal UI for managing per-server and per-tool approval modes without editing config files directly. ([#679](https://github.com/Aaronontheweb/netclaw/pull/679))

**Security**

* Patched CVE-2026-26171 and CVE-2026-33116 — `System.Security.Cryptography.Xml` is pinned to 10.0.6 to address two vulnerabilities in XML cryptography handling. ([#681](https://github.com/Aaronontheweb/netclaw/pull/681))

**Bug Fixes**

* Fixed false daemon crash alerts from SlackNet dispose race — `SlackNet`'s `ReconnectingWebSocket` can throw a `TaskCanceledException` during disposal while the daemon is shutting down, which was being surfaced as a crash alert to operators. This exception is now swallowed on the shutdown path so config hot-reloads and daemon restarts no longer generate spurious alerts. ([#680](https://github.com/Aaronontheweb/netclaw/pull/680))

* Fixed Slack routing drops logging no reason — when the routing policy silently dropped a message, operators had no way to determine which policy rule caused the drop. Routing decisions now carry a structured `IgnoreReason` that is surfaced in the structured log at the point of discard, making policy debugging actionable without source inspection. ([#682](https://github.com/Aaronontheweb/netclaw/pull/682))

#### 0.13.1 2026-04-14 ####

Netclaw v0.13.1 — llama.cpp JSON Schema compatibility fix and AGENTS.md documentation improvement

**Bug Fixes**

* Fixed SIGFAULT with Notion MCP tools on llama.cpp backends — `McpSchemaSanitizer` now strips JSON Schema keywords that llama.cpp's grammar engine cannot handle (`$schema`, `$id`, `$ref`, `$defs`, `additionalProperties`, `unevaluatedProperties`). Notion's MCP tool schemas use these keywords extensively, causing llama.cpp to SIGFAULT during grammar compilation when those tools were in scope. The sanitizer runs unconditionally on all MCP tool definitions before they reach the model. ([#664](https://github.com/Aaronontheweb/netclaw/pull/664))

**Documentation**

* Added meta-lesson on skill compression to AGENTS.md — the AGENTS.md seed now includes a "Compressing a skill into a rule is a retrieval operation" guidance block, clarifying that distilling a dotnet-skills skill or other upstream authority into an audit rule or review rubric requires opening the source first to preserve the distinctions it draws. Rules that collapse two orthogonal axes into one sentence produce false positives; if a distinction cannot be written without losing nuance, drop the rule rather than elide the distinction. ([#663](https://github.com/Aaronontheweb/netclaw/pull/663))

#### 0.13.0 2026-04-14 ####

Netclaw v0.13.0 — Subagent single-file markdown format, reminder target validation, memory observability, and hardened daemon crash handling

**Features**

* Added single-file markdown format for subagent definitions with YAML frontmatter — subagents now live in single `.md` files with YAML frontmatter instead of JSON+MD sidecar pairs. The frontmatter supports `name`, `description`, `tools`, `modelRole`, `timeoutSeconds`, `visibility`, and `emitStructuredFindings` fields. The `spawn_agent` tool now accepts an optional `context` argument that gets prefixed onto the subagent's first user message as a `Context:/Task:` block, enabling per-call specialization without mutating the agent's system prompt. The `[available-subagents]` discovery context layer is enriched with per-agent descriptions, tools, timeouts, and example invocation patterns. ([#652](https://github.com/Aaronontheweb/netclaw/pull/652))

* Added reminder notification target validation at `set_reminder` time — `SetReminderTool` now resolves `reportToChannel` through a transport-agnostic `IReminderTargetResolver` abstraction before persistence, accepting `#channel`, `@user`, and raw ID formats. Unresolvable targets fail the tool call immediately with an actionable error instead of silently saving a broken reminder that only fails when it fires. The `netclaw-operations` skill is bumped to 1.12.0 to document the new parameter contract, and `SlackReminderTargetResolver` is the single concrete implementation. Multi-transport routing is tracked separately in #644. ([#646](https://github.com/Aaronontheweb/netclaw/pull/646))

* Added memory extraction observability for silent extractor drops — turn-complete checkpoints that don't match the rules-first extractor's project-fact patterns are now logged with a categorized `MemoryExtractionDropReason` (EmptyContent, EphemeralContent, SecretLikeContent, PolicyRejected, TurnCompleteNoProjectFact, TraceNotExplicit, FingerprintDuplicate, PayloadDeserializationFailed). `MemoryCurationEngine` emits `memory_checkpoint_dropped_before_curation` at Information level with structured fields for CheckpointId, SessionId, TriggerType, IsExplicitRequest, content lengths, and drop reason. Enables operators to diagnose "why didn't this memory surface?" without source inspection. ([#653](https://github.com/Aaronontheweb/netclaw/pull/653))

* Expanded subagent delegation guidance in AGENTS.md seed — the init wizard's AGENTS.md template now lowers the delegation threshold from "deep research requiring multiple searches" to "research requiring 2+ sources or multiple searches" and explicitly calls out context-window protection as delegation's primary structural benefit. Adds a "Per-call specialization" paragraph documenting the optional `context` argument, and includes parallelization tips for spawning concurrent subagents on independent topics. Existing users' AGENTS.md files are not mutated automatically — operators should copy the new block into `~/.netclaw/identity/AGENTS.md` manually. ([#656](https://github.com/Aaronontheweb/netclaw/pull/656))

**Bug Fixes**

* Hardened daemon crash handling and diagnostics — `DaemonSupervisor` now implements more aggressive crash recovery with improved error surfacing, better state reconstruction on restart, and enhanced diagnostic logging for failure paths. Reduces daemon downtime and improves observability when crashes occur. ([#650](https://github.com/Aaronontheweb/netclaw/pull/650))

* Fixed subagent streaming LLM path dropping reasoning content — subagents using the non-streaming LLM path were having their reasoning content dropped, causing incomplete responses. Subagents now correctly use the streaming path so reasoning content is preserved in the response. ([#651](https://github.com/Aaronontheweb/netclaw/pull/651))

**Internal**

* Rewrote subagent runbook for single-file markdown format — `docs/runbooks/subagents.md` is updated end-to-end to reflect the new markdown-with-frontmatter format, enriched discovery context layer, three-argument `spawn_agent` signature, and loader behavior. Replaces the outdated JSON schema + companion-file walkthrough with a single `.md` file template. Also updates `IMPLEMENTATION_PLAN.md` line 969 to reference the new format. ([#655](https://github.com/Aaronontheweb/netclaw/pull/655))

* Consolidated anemic loader and actor tests — replaced nine near-identical subagent loader tests that reduced to `Assert.Empty(results)` with one consolidated test that drops a valid agent alongside eight kinds of invalid siblings, asserts the valid agent still loads, and verifies each invalid sibling produced a specific warning. Deletes three textbook `BuildUserMessage_*` unit tests on `SubAgentActor` that only pinned string interpolation. Net: Configuration.Tests 189 → 181, Actors.Tests SubAgent filter 30 → 27. ([#654](https://github.com/Aaronontheweb/netclaw/pull/654))

* Consolidated scattered `JsonSerializerOptions` into `JsonDefaults` — CLI code now uses a centralized `JsonDefaults` class for JSON serialization configuration instead of scattered inline options, reducing duplication and improving consistency. ([#645](https://github.com/Aaronontheweb/netclaw/pull/645))

* Documented per-argument approval granularity as security gate — skills runbook now explains how approval-mode precedence works for path-aware tool calls with argument-aware control-plane approvals, including `file_write:control-plane:netclaw.json` patterns and approve-once retry behavior. ([#642](https://github.com/Aaronontheweb/netclaw/pull/642))

#### 0.12.2 2026-04-13 ####

Netclaw v0.12.2 — Skill load telemetry, PDF attachment handling fix, and session recovery improvements

**Features**

* Added skill load telemetry by method and skill name — `SkillLoadTracker` now emits structured metrics for every skill load event, capturing the load method (local, remote, cached) and skill identifier. Enables operators to monitor skill resolution patterns and identify caching inefficiencies or remote feed latency issues. ([#637](https://github.com/Aaronontheweb/netclaw/pull/637))

**Bug Fixes**

* Fixed PDF attachments being inlined as DataContent on vision-capable models — PDF files were being converted to inline DataContent instead of being passed as file references, causing vision models to fail or misinterpret the attachment. PDFs and other document types now correctly flow through as file paths that the agent can reference explicitly. ([#638](https://github.com/Aaronontheweb/netclaw/pull/638))

* Fixed session recovery from volatile-tail loop and legacy domain NOT NULL constraint — sessions could enter an infinite loop during message assembly when the volatile tail became unstable, and legacy database records without domain values would fail on insert due to NOT NULL constraints. Both issues are resolved with defensive tail reconstruction and domain migration for legacy records. ([#634](https://github.com/Aaronontheweb/netclaw/pull/634))

* Fixed eval container missing host system skills — the behavioral eval suite running in Docker containers could not access host-mounted system skills, causing evals to fail on skill-dependent test cases. Host system skills are now properly mounted into the eval container at runtime. ([#633](https://github.com/Aaronontheweb/netclaw/pull/633))

#### 0.12.1 2026-04-13 ####

Netclaw v0.12.1 — Security hardening: MagicByteValidator extended to non-image types, structured tool-call batch metrics

**Features**

* Added `turn_tool_call_batch` structured metric logging — `LlmSessionActor` now emits a structured log entry for each tool-call batch dispatched during a turn, surfacing batch size and tool names as machine-readable fields for observability pipelines. ([#625](https://github.com/Aaronontheweb/netclaw/pull/625))

**Bug Fixes**

* Fixed MagicByteValidator rejecting PDFs, Office documents, archives, and media despite audience policy allowing them — `MagicByteValidator`'s `AllowedExtensions` previously only accepted image types (PNG/JPG/GIF/WebP), so every non-image attachment sent to a Team or Personal audience was rejected at the content scanner even though `ChannelAttachmentPolicy` had already permitted it at the policy layer. The validator is now rewritten around a MIME-keyed signature-rule table covering every category advertised by the Team audience: PDF, OOXML/ODF, legacy OLE Office, plain/structured text, RTF, zip/7z/rar/gzip/bzip2/xz, and mp3/mp4/wav/ogg/avi/webm/mkv. Each matcher is hardened with type-specific magic-byte checks beyond the minimum header. `ContentPolicy.DefaultAllowedMimeTypes` is seeded from the validator's supported set so the two layers cannot drift, and `DefaultMaxFileSizeBytes` is raised from 20 MB to 25 MiB to match `ChannelAttachmentPolicy`. ([#626](https://github.com/Aaronontheweb/netclaw/pull/626))

#### 0.12.0 2026-04-13 ####

Netclaw v0.12.0 — KV cache-friendly session routing, Slack PDF/document ingress, durable working context, structured compaction, and security hardening

**Features**

* Added cache-stable message assembly with a volatile User-role tail — memory recall, current time, and working-context layers used to be injected as System messages immediately after the persisted prompt, which broke llama.cpp prompt-cache prefix matching on every turn. A new `SessionMessageAssembler` now partitions outgoing messages so cache-stable content (persisted prompt, static dynamic context, conversation history) comes first and volatile content is consolidated into a single User-role tail. Combined with session-sticky routing (#610), multi-turn conversations reuse the KV cache across turns. ([#618](https://github.com/Aaronontheweb/netclaw/pull/618))

* Added session-sticky LLM routing via `X-Session-Id` header — self-hosted inference servers behind a load balancer no longer defeat KV cache reuse across turns. A `DelegatingHandler` promotes the ambient session ID to an `X-Session-Id` HTTP header, which the load balancer can hash on (e.g., Caddy `lb_policy header X-Session-Id`) to pin same-session requests to the same backend GPU. Works automatically for all providers; managed providers (Anthropic, OpenAI, OpenRouter) safely ignore the header. Sidecar calls (compaction, title generation, memory extraction) bypass the header so they don't compete with the main session's cache slot. ([#610](https://github.com/Aaronontheweb/netclaw/pull/610))

* Added llama.cpp `timings` parsing for cache and performance metrics — the OpenAI-compatible provider now surfaces KV cache hit counts (`cache_n`), prompt processing time (`prompt_ms`), and predicted throughput (`predicted_per_second`) from llama.cpp responses. New fields (`cachedInputTokens`, `promptMs`, `predictedPerSecond`, `ttftMs`, `totalMs`) flow through `UsageOutput`, the SignalR wire DTO, and the `chat -p --json` CLI envelope. Graceful degradation: all fields stay null for providers that don't emit `timings`. ([#615](https://github.com/Aaronontheweb/netclaw/pull/615))

* Added `--resume` and `-p` flags to `netclaw chat` for scripted multi-turn sessions — `netclaw chat -p "prompt"` replaces the old top-level `netclaw -p` form, and `netclaw chat -p --resume <id> "prompt"` creates-or-resumes a named session so multi-turn evals, KV cache benchmarking, and compaction regression tests can run against the same session across turns. `netclaw chat -p --json` emits structured JSON (`sessionId`, `response`, `toolCalls`, `usage`) for machine consumption. ([#613](https://github.com/Aaronontheweb/netclaw/pull/613))

* Added audience-gated attachment ingress contract with Slack PDF support — PDFs, DOCX files, and other non-image attachments were silently dropped from Slack messages with no feedback to the user. This release introduces a cross-channel `ChannelAttachmentPolicy` on `ToolAudienceProfile` with `AllowedCategories`, `MaxFileBytes` (default 25 MiB), and `MaxFilesPerMessage` (default 10). Public audiences allow images only; Team audiences allow everything except unknown binaries. Slack now downloads and surfaces non-image attachments consistently, and the agent gets a dynamic system-prompt hint explaining what it received and whether the active model can view it. ([#601](https://github.com/Aaronontheweb/netclaw/pull/601))

* Added durable `WorkingContext` grounding that survives compaction and restart — sessions now persist a `WorkingContext` field alongside conversation state, containing `RecentFiles` (bounded ring buffer of 10, updated automatically by file-taking tools), `OpenGoals`, and `ProgressMarkers`. Unlike observer-reconstructed context, `WorkingContext` survives compaction, actor recovery, and daemon restart without depending on the observer LLM. ([#598](https://github.com/Aaronontheweb/netclaw/pull/598))

* Added structured compaction summary with monotonic boundary — session compaction now uses a 9-section structured template (adopted from Cline's LLM harness) with explicit anti-drift rules, truncate-only-at-user-message-boundaries enforcement (adopted from OpenCode), and self-session-id disambiguation so the observer can mark foreign session IDs from tool-call history. `SessionState.CompactionBoundaryIndex` provides monotonic metadata pointing at the most recent session-summary marker. Fixes a Slack failure where a session lost its own ID after a compaction following a turn investigating another session. ([#597](https://github.com/Aaronontheweb/netclaw/pull/597))

* Containerized `netclawd` and moved the behavioral eval suite into ephemeral Docker — `docker/Dockerfile` produces a release-grade `ubuntu:24.04` image pre-installed with `git`, `jq`, `sqlite3`, `python3`, `gh`, and the `netclaw` + `netclawd` binaries. The eval suite now runs against a fresh container per invocation, so evals no longer contaminate the operator's real `~/.netclaw` state with seeded test docs and LLM-formed memories. The same image is the supported Docker-deployment artifact for self-hosted `netclawd`. ([#603](https://github.com/Aaronontheweb/netclaw/pull/603))

* Added multi-turn conversation eval category and Personal posture for the eval container — new Category 8 exercises `chat -p --resume` across 2-5 turn scripted sessions and captures per-turn KV cache and timing metrics. Adds a "Multi-Turn Cache Evolution" report section showing cached vs uncached token trending per turn, plus fixes a latent eval container misconfiguration that was silently degrading shell-using cases to tool-call-marker-only checks. ([#616](https://github.com/Aaronontheweb/netclaw/pull/616))

**Bug Fixes**

* Fixed control-plane writes bypassing approval policy — agents could silently edit `~/.netclaw/config/netclaw.json` and other control-plane files, which could trigger daemon restart and drop the active session mid-turn. `ToolPathPolicy` is now split into three independent deny surfaces (write-deny, read-deny, shell-indicators), and a new `FilePathApprovalMatcher` supports argument-aware control-plane approvals with path-scoped patterns like `file_write:control-plane:netclaw.json`. Approval-mode precedence is now deterministic for path-aware calls, and approve-once retry uses the same filtered unapproved pattern set the user saw in the prompt. Shell resource deny coverage is extended to lifecycle files (`netclaw.db`, `netclaw.pid`, `netclaw.lock`, `cache/restart-manifest.json`). ([#617](https://github.com/Aaronontheweb/netclaw/pull/617))

* Fixed Slack attachment contract parity and hardening — `SlackThreadHistoryFetcher` no longer hard-filters thread-history attachments to `image/*`, so historical PDFs, DOCX files, and other non-image attachments are now downloaded and scanned consistently with live ingress. `SlackThreadBindingActor` replaces raw `ex.Message` in all user-facing rejected paths with stable messages (exception detail stays in structured logs). `EscapeQuoted` now strips control characters so hostile filenames can no longer embed newlines in the `[attachment]` announcement line. ([#607](https://github.com/Aaronontheweb/netclaw/pull/607))

* Fixed Claude Code marketplaces and `~/.claude/commands` skill resolution — the `claude-code` source alias now resolves to `~/.claude/skills`, `~/.claude/commands`, and every installed marketplace under `~/.claude/plugins/marketplaces/*/skills/`, so marketplace skills like dotnet-skills are discoverable and `~/.claude/commands` support is restored. Frontmatterless flat `.md` files are accepted only for the Claude commands path, matching current Claude Code behavior; non-commands paths still require valid YAML frontmatter. ([#599](https://github.com/Aaronontheweb/netclaw/pull/599))

* Fixed system skills feed serving a stale manifest to daemons — `publish_skills.yml` was running `wrangler pages deploy` without pinning a branch, so release-tag builds (which run in detached HEAD) shipped the new manifest to a `head.netclaw-feeds.pages.dev` preview alias instead of the production custom domain. `feeds.netclaw.dev` had been frozen on the 2026-03-20 manifest for two releases, causing 5 of 6 skills to 404 on every daemon sync. The deploy now pins `--branch=dev` and a post-deploy propagation check asserts the published `updatedAt` matches the freshly generated manifest. ([#606](https://github.com/Aaronontheweb/netclaw/pull/606))

**Internal**

* Disabled the `publish-docker` job in the release pipeline — Docker image publishing to GHCR is gated off until the Docker release path is green-lit. `publish-binaries` and the Cloudflare Pages manifest job are unaffected, and `validate_docker_image.yml` (PR build, no push) is untouched. ([#620](https://github.com/Aaronontheweb/netclaw/pull/620))

#### 0.11.0 2026-04-11 ####

Netclaw v0.11.0 — Slack thread backfill, approval flow fixes, memory recall improvements, and internal cleanup

**Features**

* Added thread history backfill on mid-thread @-mention — when Netclaw is mentioned mid-thread (rather than at the start), it now fetches and hydrates the full prior thread history so the LLM has complete context for the conversation. ([#576](https://github.com/Aaronontheweb/netclaw/pull/576))

* Added inbound webhook observability and stats surface — the webhooks ingress layer now exposes per-route call counters, last-call timestamps, and error rates, giving operators a live view of webhook activity without digging through logs. ([#550](https://github.com/Aaronontheweb/netclaw/pull/550))

**Bug Fixes**

* Fixed approval wait handling and Slack prompt resolution after selection — approval flows could hang or leave stale interactive prompts in Slack after a user selected an option. Both issues are resolved. ([#587](https://github.com/Aaronontheweb/netclaw/pull/587))

* Fixed missing gap image in Slack thread hydration — `DataContent` for gap images was dropped during thread reconstruction, causing broken display when Netclaw replayed thread history. ([#583](https://github.com/Aaronontheweb/netclaw/pull/583))

* Fixed reminder notifications using a hardcoded audience instead of the deployment posture audience — reminders now correctly derive the notification audience from the active deployment posture rather than always targeting `Team`. ([#570](https://github.com/Aaronontheweb/netclaw/pull/570))

* Fixed memory recall under-returning results — recall overfetch and formation over-production were reduced; the composite-score ranker now applies a score floor so weak matches are excluded rather than surfaced. ([#567](https://github.com/Aaronontheweb/netclaw/pull/567), [#582](https://github.com/Aaronontheweb/netclaw/pull/582), [#585](https://github.com/Aaronontheweb/netclaw/pull/585))

* Fixed per-channel memory domains collapsing to a shared namespace — memory was written to `project:default` instead of the per-channel domain, causing cross-channel knowledge bleed. Channels now have properly isolated memory audiences. ([#557](https://github.com/Aaronontheweb/netclaw/pull/557))

* Fixed `netclaw init` wizard issues from 0.10.1 — resolved several init wizard regressions including config write ordering and step skip logic. ([#537](https://github.com/Aaronontheweb/netclaw/pull/537), [#538](https://github.com/Aaronontheweb/netclaw/pull/538), [#540](https://github.com/Aaronontheweb/netclaw/pull/540), [#559](https://github.com/Aaronontheweb/netclaw/pull/559))

* Fixed LLM sessions not draining on SIGTERM — active sessions were abandoned rather than drained during shutdown, causing stale active session counts to persist after restart. Sessions are now given a grace period to complete their current turn before the process exits. ([#564](https://github.com/Aaronontheweb/netclaw/pull/564))

* Fixed CLI endpoint state being entangled with daemon config — CLI connection state is now tracked separately from the daemon's runtime configuration, preventing config mutations from affecting active CLI sessions. ([#556](https://github.com/Aaronontheweb/netclaw/pull/556))

* Hardened skill scanning — duplicate frontmatter names from the same source are now rejected in non-strict mode, and several edge cases in skill directory scanning are closed out. ([#552](https://github.com/Aaronontheweb/netclaw/pull/552), [#555](https://github.com/Aaronontheweb/netclaw/pull/555))

**Internal**

* Removed dead memory migration cruft, sidecar infrastructure, HardScope plumbing, and the Domain concept in favor of proper audience isolation. These are internal refactors with no user-visible behavior changes. ([#586](https://github.com/Aaronontheweb/netclaw/pull/586), [#588](https://github.com/Aaronontheweb/netclaw/pull/588), [#589](https://github.com/Aaronontheweb/netclaw/pull/589))

**Dependencies**

* Bumped Anthropic SDK from 12.11.0 to 12.13.0. ([#571](https://github.com/Aaronontheweb/netclaw/pull/571))

#### 0.10.0 2026-04-03 ####

Netclaw v0.10.0 — Remote access foundation (exposure modes, device pairing, hub auth), inbound webhooks, and skill management CLI

**Features**

* Added exposure modes, hub authentication, and device pairing — Netclaw can now run in `Local`, `Tailscale`, `Cloudflare`, or `Ngrok` exposure modes, controlling how the hub is reachable from remote devices. Hub authentication uses a multi-scheme pipeline that routes between loopback and device-token handlers based on connection origin. Device pairing uses single-use 5-minute pairing codes with salted SHA-256 token storage; paired devices are managed via `netclaw pair`, `netclaw devices`, and `netclaw devices revoke` CLI commands. Non-local exposure modes require at least one paired device or configured auth scheme at startup. ([#523](https://github.com/Aaronontheweb/netclaw/pull/523))

* Added inbound webhooks with hot-reloaded route files — external services can now push events into Netclaw via verified webhook routes stored as JSON files under `~/.netclaw/config/webhooks/`. Routes are hot-reloaded on file change with fail-closed invalidation: malformed or invalid route files are rejected and trigger operational alerts rather than silently degrading. Includes generic verified ingress for autonomous webhook sessions and dedicated webhook admin tools for managing secret-bearing route config. `netclaw doctor` validates webhook route files. ([#525](https://github.com/Aaronontheweb/netclaw/pull/525))

* Added inbound webhook toggle to `netclaw init` wizard — the exposure-mode wizard step now includes a sub-step to enable or disable inbound webhook ingestion. The toggle appears after mode selection, and the copy clearly distinguishes inbound webhook ingestion from outbound notification webhooks. Enabled webhooks write `Webhooks.Enabled = true` to `netclaw.json`. ([#531](https://github.com/Aaronontheweb/netclaw/pull/531))

* Added `netclaw skill` CLI command family for offline skill management — new subcommands include `list` (table of all skills with source/version/status), `show` (display metadata and content), `validate` (check SKILL.md format), `remove` (delete native skills), `issues` (show scanner issues), `search` (substring search across all sources), and `source list/add/remove/enable/disable` (manage external skill sources in config). ([#534](https://github.com/Aaronontheweb/netclaw/pull/534))

* Added `SkillDirectoryWatcherService` for automatic skill rescanning — a `BackgroundService` with `FileSystemWatcher` monitors native and external skill directories for changes, triggering a debounced rescan without requiring daemon restart. Ignores `.staging/` and `.tmp` files from atomic write operations. ([#534](https://github.com/Aaronontheweb/netclaw/pull/534))

* Added flat-file `.md` skill support — `SkillScanner` now accepts single `.md` files with valid YAML frontmatter as first-class skills, enabling compatibility with Claude Code's `~/.claude/skills/` flat-file format. Directory skills take precedence over flat files with the same name. ([#534](https://github.com/Aaronontheweb/netclaw/pull/534))

**Bug Fixes**

* Improved memory deduplication accuracy — content-based search now runs whenever there is no exact anchor match (not just zero matches), catching semantically identical content stored under different anchor names. Anchor matching now uses prefix-aware comparison ("repo" matches "repository", min 3-char prefix) and allows up to 2-token differences. Ambiguous dedup decisions (40-80% overlap) are auto-resolved to Skip when content overlap is 60% or higher and anchor Jaccard is 50% or higher, instead of always defaulting to Create when the LLM tier is unavailable. ([#535](https://github.com/Aaronontheweb/netclaw/pull/535))

**Dependencies**

* Bumped Anthropic SDK from 12.9.0 to 12.11.0. ([#526](https://github.com/Aaronontheweb/netclaw/pull/526))

#### 0.9.4 2026-04-01 ####

Netclaw v0.9.4 — Conditional reminder notifications, llama.cpp MCP schema compatibility, and MCP SDK 1.2.0

**Features**

* Added `NotificationPolicy` for conditional reminder notifications — reminders can now be created with `NotifyPolicy: Conditional`, allowing the LLM to skip the notification tool call without the execution being marked as failed. Useful for reminders like "only notify if there are actionable results." Failed notification attempts still count as failures regardless of policy. Existing reminders without a `notifyPolicy` field default to `Required`, preserving current behavior. ([#512](https://github.com/Aaronontheweb/netclaw/pull/512))

**Bug Fixes**

* Fixed MCP tool schemas breaking llama.cpp grammar generation — `$schema` meta-references and `additionalProperties: {}` empty objects in MCP tool parameter schemas caused immediate 502 errors when loading tools from servers like Notion when using llama.cpp as the backend. Schemas are now sanitized at registration time: `$schema` references are stripped and empty-object `additionalProperties` values are normalized to `true`. Sanitization applies recursively through all schema keywords. ([#516](https://github.com/Aaronontheweb/netclaw/pull/516))

**Dependencies**

* Bumped ModelContextProtocol.Core from 1.1.0 to 1.2.0. This upstream release **disables legacy SSE endpoints by default** — MCP servers that previously served `/sse` and `/message` endpoints will no longer expose them unless `HttpServerTransportOptions.EnableLegacySse = true` is set explicitly. Clients using the legacy SSE transport must migrate their endpoint URL from `/sse` to the root MCP endpoint. See the [MCP C# SDK 1.2.0 release notes](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v1.2.0) for full migration guidance. ([#478](https://github.com/Aaronontheweb/netclaw/pull/478))

#### 0.9.3 2026-03-31 ####

Netclaw v0.9.3 — External skill directories, per-tool MCP audience gating, and tool-loop compaction fix

**Features**

* Added support for loading skills from external directories — operators can now configure `ExternalSkills.Sources` in `netclaw.json` to load skills from Claude Code (`~/.claude/skills/`), Open Code, or any custom team directory alongside native Netclaw skills. Per-source `AllowSymlinks` policy controls whether symlinked skill files are followed. External directories are read-only — `skill_manage` rejects all write operations targeting them. Native skills win on name collision. ([#503](https://github.com/Aaronontheweb/netclaw/pull/503))

* Added per-tool audience gating for MCP servers — `ToolAudienceProfile` now accepts a `McpServerToolGrants` dictionary that restricts which tools from a given MCP server are exposed to that audience. Composes with the existing `AllowedMcpServers` gate; omitting grants preserves the current behavior of exposing all tools. New `netclaw mcp tools <server>` CLI command shows the per-audience grant status table, and `--snapshot` baselines grants from the currently discovered tool set. ([#500](https://github.com/Aaronontheweb/netclaw/pull/500))

* Added `netclaw init` wizard step to detect and suggest external skill directories — the init wizard now probes for well-known skill directories (`~/.claude/skills`, `~/.open-code/skills`) and presents a checklist to toggle each detected source. The step auto-skips when neither directory exists on disk. Detected sources are written into the `ExternalSkills.Sources` config section. ([#509](https://github.com/Aaronontheweb/netclaw/pull/509))

* Added MCP tool description capping and configurable schema size warnings — tool descriptions are capped at 2KB at registration time (matching Claude Code's documented limit) to prevent oversized MCP descriptions from bloating the LLM context window. Operators are warned via log when tool schemas exceed 8KB. Both thresholds are configurable via `SessionTuning.MaxToolDescriptionChars` and `SessionTuning.MaxToolSchemaWarnChars`. ([#498](https://github.com/Aaronontheweb/netclaw/pull/498))

**Bug Fixes**

* Fixed compaction never triggering during tool-loop iterations — `_lastInputTokenCount` was updated for metrics but never written to the field checked by `ShouldCompact()`, so the threshold was never crossed during multi-step tool loops. Compaction now checks after each tool response, and the tool loop resumes automatically after mid-loop compaction completes. Closes [#424](https://github.com/Aaronontheweb/netclaw/issues/424). ([#511](https://github.com/Aaronontheweb/netclaw/pull/511))

* Fixed delivery retry eligibility being silently lost on session passivation — `DeliveryRetryHandler._eligibleTurnNumber` was in-memory only; if a channel reported delivery failure after the session stopped, the recovered instance discarded the feedback as stale and silently dropped the retry. Eligibility is now persisted in `SessionSnapshot` and restored on recovery. Backward-compatible with existing snapshots. ([#504](https://github.com/Aaronontheweb/netclaw/pull/504))

* Evict discovered MCP tools on compaction — discovered tools survived compaction and continued consuming context even after the conversation was summarized. Tools are now evicted alongside other compaction resets, forcing re-discovery of only the tools actually needed for the next turn. ([#498](https://github.com/Aaronontheweb/netclaw/pull/498))

#### 0.9.2 2026-03-31 ####

Netclaw v0.9.2 — Schema-driven config migration, load_tool two-step discovery, and security model simplification

**Features**

* Added schema-driven config migration to `netclaw doctor --fix` — a new `SchemaFixResolver` validates the config against the JSON schema and automatically corrects three common error patterns: integer-to-string enum coercion (stale numeric enum values), missing required property insertion (when the schema defines a default), and stale property removal (properties disallowed by `additionalProperties: false`). The `doctor` command now shows exactly which fixes were applied and suggests `--fix --dry-run` when fixable issues are detected. ([#493](https://github.com/Aaronontheweb/netclaw/pull/493))

**Bug Fixes**

* Fixed `search_tools` auto-loading all MCP tool schemas into the session, causing immediate 502 failures when large schema sets (e.g., Notion's 45K-char schema) were discovered — `search_tools` is now discovery-only and returns names and descriptions without loading schemas. A new `load_tool` built-in lets the LLM explicitly activate individual tools on demand. Discovered tools are also evicted on `LlmCallFailed` to prevent poisoned tool sets from cascading across turns. ([#492](https://github.com/Aaronontheweb/netclaw/pull/492))

**Breaking Changes**

* Removed `CapabilityClass` from MCP server config — the `McpCapabilityClass` enum has been removed and `CapabilityClass` is no longer a valid property on MCP server entries. MCP tool exposure is now gated solely by `AllowedMcpServers` in audience profiles. Configs containing `CapabilityClass` will be flagged by `netclaw doctor` and can be auto-removed with `netclaw doctor --fix`. ([#491](https://github.com/Aaronontheweb/netclaw/pull/491))

#### 0.9.1 2026-03-30 ####

Netclaw v0.9.1 — Configurable project workspaces, reminders upgrade, skill security scanning, and session reliability fixes

**Features**

* Added configurable project workspaces with AGENTS.md discovery — operators can now define named workspaces under `~/.netclaw/workspaces/`. Each workspace maps a name to a root directory, and `AGENTS.md` files found in the workspace tree are surfaced automatically as project context. The `netclaw init` wizard now prompts for a workspaces path during setup. ([#472](https://github.com/Aaronontheweb/netclaw/pull/472), [#482](https://github.com/Aaronontheweb/netclaw/pull/482))
* Upgraded to Akka.Reminders v0.6.0-beta2 with envelope-based delivery — reminders now deliver via a typed envelope wrapper rather than raw message dispatch, improving type safety and routing clarity. **Breaking change:** the SQLite reminder store schema adds ack-tracking columns in this release. Existing reminder databases must be deleted and recreated after upgrading. ([#484](https://github.com/Aaronontheweb/netclaw/pull/484))
* Added skill content security scanning with shared prompt injection detection — skill files are now scanned at load time using 22 regex patterns across 5 threat categories (prompt injection, role hijacking, instruction override, data exfiltration, and system escape). Skills that match a pattern are blocked from loading and logged with the specific pattern that triggered the rejection. ([#408](https://github.com/Aaronontheweb/netclaw/pull/408))

**Bug Fixes**

* Fixed sessions requiring manual message resend after context overflow compaction — sessions now automatically resend the original user message after compaction completes, so users no longer see "Please resend your last message" prompts. Also added streaming retry for transient provider errors (5xx, 429) with configurable backoff via `SessionTuning.StreamingRetryPolicy`. ([#485](https://github.com/Aaronontheweb/netclaw/pull/485))
* Fixed `HttpClient.Timeout` (default 100s) prematurely killing LLM calls for self-hosted models with large contexts — the session watchdog is now the sole timeout authority for LLM request lifetimes. `HttpClient.Timeout` is set to `Timeout.InfiniteTimeSpan` and all cancellation flows through the session watchdog's `CancellationToken`. ([#476](https://github.com/Aaronontheweb/netclaw/pull/476))
* Fixed reminder execution actor using the wrong output filter — the actor was configured with `TextStreaming | ToolCalls` instead of the correct filter for reminder delivery, causing reminders to not stream output correctly. ([#475](https://github.com/Aaronontheweb/netclaw/pull/475))

#### 0.9.0 2026-03-27 ####

Netclaw v0.9.0 — Two-phase streaming timeout, FTS5 memory recall, turn-based distillation, and streaming performance fix

**Features**

* Added two-phase streaming timeout for LLM calls — introduces separate `FirstTokenTimeout` and `StreamIdleTimeout` settings, replacing the single monolithic timeout. The first-token window catches provider failures fast while the idle timeout tolerates long-running tool-use streams without premature cancellation. ([#460](https://github.com/Aaronontheweb/netclaw/pull/460))
* Added turn-based memory distillation trigger alongside idle timeout — sessions now trigger memory distillation at turn boundaries in addition to the existing 90-second idle timer, so busy multi-turn conversations no longer defer all memory formation until the session goes quiet. ([#442](https://github.com/Aaronontheweb/netclaw/pull/442), [#463](https://github.com/Aaronontheweb/netclaw/pull/463))
* Migrated memory recall search from LIKE to SQLite FTS5 full-text search with BM25 scoring — recall queries now use a dedicated FTS5 virtual table for relevance-ranked results instead of substring matching, significantly improving recall quality for multi-word queries. ([#436](https://github.com/Aaronontheweb/netclaw/pull/436))
* Added token delta and cumulative logging to `LoggingChatClient` — each streaming chunk now logs its individual token delta alongside the running cumulative total, making it easier to diagnose token consumption during long tool-use loops. ([#443](https://github.com/Aaronontheweb/netclaw/pull/443), [#445](https://github.com/Aaronontheweb/netclaw/pull/445))

**Bug Fixes**

* Fixed daemon restarting active sessions during config reload — the config restart path now drains all active sessions gracefully before applying the new configuration, preventing mid-turn interruptions. ([#468](https://github.com/Aaronontheweb/netclaw/pull/468))
* Fixed `web_fetch` saving binary content with incorrect extensions and corrupted bytes — binary responses (images, PDFs, etc.) are now saved with the correct file extension derived from the Content-Type header, and raw bytes are written without text encoding round-trips. ([#386](https://github.com/Aaronontheweb/netclaw/pull/386), [#461](https://github.com/Aaronontheweb/netclaw/pull/461))
* Enforced HTTPS by default in the `web_fetch` tool — requests to plain HTTP URLs are now rejected unless explicitly allowed, hardening the default security posture for outbound fetches. ([#458](https://github.com/Aaronontheweb/netclaw/pull/458))
* Fixed `netclaw init` auto-detecting Slack webhook format from URL — the init wizard now inspects the webhook URL to determine whether it is a Slack incoming webhook and sets `"Format": "Slack"` automatically, eliminating a common misconfiguration. ([#401](https://github.com/Aaronontheweb/netclaw/pull/401), [#462](https://github.com/Aaronontheweb/netclaw/pull/462))
* Stopped persisting system prompt in Akka journal — the full system prompt was previously included in persisted session state, bloating the journal and slowing recovery. It is now excluded from persistence and reconstructed on recovery. ([#451](https://github.com/Aaronontheweb/netclaw/pull/451))
* Enforced record-class storage for evidence memories — evidence memory proposals that arrived as plain strings are now normalized into the expected record-class format before storage, preventing downstream deserialization failures. ([#446](https://github.com/Aaronontheweb/netclaw/pull/446), [#452](https://github.com/Aaronontheweb/netclaw/pull/452))
* Added `TurnOutcome` to prevent quick-exit paths from inflating turn count — tool-only and no-op turns that exit early without an LLM response no longer increment the turn counter, giving more accurate turn-based metrics. ([#450](https://github.com/Aaronontheweb/netclaw/pull/450))
* Added singleton guard and PID file watchdog to prevent duplicate daemons — launching a second daemon instance now fails fast with a clear error instead of silently competing for resources. A PID file watchdog detects stale lock files from crashed processes. ([#433](https://github.com/Aaronontheweb/netclaw/pull/433))
* Fixed console output leaks in CLI and daemon — stray `Console.Write` calls that bypassed the structured logging pipeline are now routed through the proper output channels. ([#425](https://github.com/Aaronontheweb/netclaw/pull/425))

**Performance**

* Fixed O(n^2) `ToolCallTextFilter` scanning during streaming — replaced the quadratic per-chunk scan with incremental delta detection, eliminating a hot path that caused visible latency on long tool-call sequences. ([#454](https://github.com/Aaronontheweb/netclaw/pull/454), [#457](https://github.com/Aaronontheweb/netclaw/pull/457))

**Diagnostics**

* Unified daemon uptime into a single `DaemonStartClock` singleton — previously uptime was computed from multiple inconsistent sources. All uptime queries now read from one authoritative clock. ([#456](https://github.com/Aaronontheweb/netclaw/pull/456))
* Surfaced diagnostic detail when update check fails — `netclaw update` and background update checks now log the specific HTTP status and error body instead of a generic "check failed" message. ([#431](https://github.com/Aaronontheweb/netclaw/pull/431))

**Dependencies**

* Bumped ModelContextProtocol.Core from 1.0.0 to 1.1.0. ([#434](https://github.com/Aaronontheweb/netclaw/pull/434))

#### 0.8.1 2026-03-25 ####

Netclaw v0.8.1 — Slack duplicate message fix, memory passivation hardening, file_edit tool, and LlmSessionActor decomposition

**Features**

* Added `file_edit` tool for surgical text replacements — enables targeted edits without full file rewrites. Supports literal text matching with an ambiguity guard that rejects non-unique matches, a `ReplaceAll` option for bulk replacements, and the same security enforcement as `file_write`. ([#404](https://github.com/Aaronontheweb/netclaw/pull/404), [#416](https://github.com/Aaronontheweb/netclaw/pull/416))

**Bug Fixes**

* Fixed duplicate Slack messages caused by Microsoft.Extensions.AI 10.4.1 preserving non-contiguous `TextContent` items — the Slack output handler now consolidates multiple `TextContent` items into a single `TextOutput` before posting, preventing repeated message fragments in threads. ([#413](https://github.com/Aaronontheweb/netclaw/pull/413), [#429](https://github.com/Aaronontheweb/netclaw/pull/429))
* Fixed `SessionMemoryObserverActor` passivation protocol — resolved dead-lettered phase notifications, mid-distillation reply drops causing 5-second stalls, and a racing idle timer during passivation. Also replaced hardcoded `DateTimeOffset.UtcNow` with injected `TimeProvider` for testability. ([#423](https://github.com/Aaronontheweb/netclaw/pull/423), [#428](https://github.com/Aaronontheweb/netclaw/pull/428))

**Architecture**

* Decomposed `LlmSessionActor` for composability — split `SessionConfig` into `ModelCapabilities`, `SessionTuning`, and `SessionConfig` value objects, reduced the actor's constructor from 19 to 7 parameters, formalized the session lifecycle with a `SessionPhase` enum-based state machine, and extracted 9 focused handler modules. ([#411](https://github.com/Aaronontheweb/netclaw/pull/411), [#414](https://github.com/Aaronontheweb/netclaw/pull/414), [#417](https://github.com/Aaronontheweb/netclaw/pull/417))

#### 0.8.0 2026-03-25 ####

Netclaw v0.8.0 — Memory pipeline overhaul, trust context policy, Slack message delivery fix, and update signature verification

**Memory**

* Overhauled memory formation, recall, and curation — stopped raw tool outputs from polluting the memory database (previously ~71% of records were junk web_search/web_fetch dumps), cached recall resolution at turn boundaries instead of re-running on every tool-loop LLM call, and added exclusion-based progressive recall so each turn surfaces different relevant memories instead of repeating the same top results. ([#380](https://github.com/Aaronontheweb/netclaw/pull/380))
* Added session-level memory observer actor — a persistent child actor watches the conversation stream and distills memories when the session goes idle (90s), replacing the previous per-turn observation that produced zero proposals. Proposals flow through the existing gate/curation pipeline, and the observer journals proposed anchors so its skip list survives across actor incarnations. ([#410](https://github.com/Aaronontheweb/netclaw/pull/410))

**Security**

* Added minisign Ed25519 signature verification to `netclaw update` and all background update checks — the manifest is now verified against an embedded public key before trusting its contents. Missing or invalid signatures fail closed with no fallback to unsigned manifests. Background update checks now run on a 24-hour periodic timer and emit `UpdateAvailable` operational alerts via webhooks. ([#393](https://github.com/Aaronontheweb/netclaw/pull/393))
* Added trust context policy with channel audiences and global read roots — operators can now configure per-channel audience overrides in `Slack.ChannelAudiences` (resolution: explicit channel ID, then `"dm"` key, then heuristic fallback). Skills and identity files are always readable regardless of audience profile via `GlobalReadRoots`. The init wizard now generates a `Security` section with smart defaults derived from deployment posture, and `netclaw doctor` enforces that both `Security` and `Tools` config sections are present. ([#387](https://github.com/Aaronontheweb/netclaw/pull/387))

**Slack**

* Fixed Slack sessions receiving dropped final responses and duplicate message posts — the root cause was `TextDeltaOutput` (streaming) and `TextOutput` (final) both emitting under the same `OutputFilter.Text` flag. Added a new `OutputFilter.TextStreaming` flag so Slack subscribes only to final assembled text while the TUI continues to receive both streams. Also fixed slash-command IO failures silently falling through to the LLM instead of surfacing an error. ([#407](https://github.com/Aaronontheweb/netclaw/pull/407))

**Skills**

* Replaced keyword-based skill matching with LLM-driven description menu — the skill index context layer now presents skill descriptions directly to the LLM, which loads relevant skills via `file_read`. Removed the entire keyword matching pipeline including IDF scoring, keyword enrichment sidecar, keyword cache I/O, and auto-load state from `LlmSessionActor`. ([#380](https://github.com/Aaronontheweb/netclaw/pull/380))
* Skill enrichment now runs in the background instead of blocking daemon startup, fixing startup and healthcheck timeouts on slower hardware or when multiple skills need enrichment. ([#407](https://github.com/Aaronontheweb/netclaw/pull/407))
* **Breaking (operators with custom skill references):** System skills consolidated from 6 to 4 — `netclaw-diagnostics`, `netclaw-identity`, and `netclaw-manual` were removed and their content merged into `netclaw-operations` and the remaining skills. Operators referencing the old skill names in custom configurations or scripts will need to update those references. ([#380](https://github.com/Aaronontheweb/netclaw/pull/380))

**CLI / TUI**

* Fixed the provider manager TUI crashing when two providers shared the same type (e.g., two `openai-compatible` entries with different endpoints) — the display now shows all configured instances and supports adding additional instances of any type. ([#385](https://github.com/Aaronontheweb/netclaw/pull/385))
* Renamed the `openai-compatible` provider display from "OpenAI-Compatible" to "llama.cpp / vLLM" to better describe the actual servers it supports. No changes to config format or internal type keys. ([#383](https://github.com/Aaronontheweb/netclaw/pull/383))

**Dependencies**

* Bumped Akka.NET to 1.5.63 — includes a critical Akka.Remote fix for stale ACKs causing irrecoverable quarantine after transient network disruptions. All users running clustered deployments should upgrade. ([#397](https://github.com/Aaronontheweb/netclaw/pull/397))
* Bumped Microsoft.Extensions.AI packages to 10.4.1 and adapted to OpenAI SDK 2.9.1 breaking change where `ResponsesClient` no longer accepts a model parameter in its constructor. ([#392](https://github.com/Aaronontheweb/netclaw/pull/392))

#### 0.7.9 2026-03-21 ####

Netclaw v0.7.9 — Slack delivery hardening, config watcher fix, webhook schema fix, and token usage fix

**Slack**

* Fixed Slack delivery failures being silently swallowed — `SlackReplyClient` was discarding the return value of `Chat.PostMessage()` and relying on SlackNet to throw on `ok:false`. A bug in SlackNet 0.17.9 causes phantom `Ok=true` responses when the HTTP body is empty, so delivery failures were counted as successes. Responses are now validated — null or empty `Ts` throws `SlackMessageDeliveryException` with a `phantom_success` error code, and the full error is fed back to the LLM session. Added `SlackException` catch and response validation to `SlackOutboundClient`, which previously had zero error handling. `netclaw stats` now shows `posted`/`rejected`/`failed` counters instead of the old `posted`/`failed`/`plain_text_fallback` columns. ([#375](https://github.com/Aaronontheweb/netclaw/pull/375))

**Config**

* Fixed OAuth token refreshes causing daemon restarts mid-session — `ConfigWatcherService` was watching both `netclaw.json` and `secrets.json`, so writing a refreshed OAuth token triggered a full daemon restart, killing active turns. The watcher now monitors only `netclaw.json`; secret changes are loaded on-demand and never require a restart. ([#373](https://github.com/Aaronontheweb/netclaw/pull/373))
* Fixed `netclaw doctor` rejecting configs with `"Format": "Slack"` on webhook targets — the `Format` field was added to `WebhookTarget` but omitted from `netclaw-config.v1.schema.json`, which uses `"additionalProperties": false` throughout. Any `netclaw.json` that set `"Format": "Slack"` on a webhook was flagged as invalid by the schema doctor check. The schema now includes the `Format` enum and the field is optional (defaults to `Generic`). ([#369](https://github.com/Aaronontheweb/netclaw/pull/369))

**Stats**

* Fixed `netclaw stats` always showing zero token usage — OpenAI-compatible providers send token counts in a final SSE chunk that has an empty `choices` array. `ParseStreamingUpdates` returned early on empty choices before reaching `ParseUsage`, silently discarding all token data. Usage-only chunks are now processed before the early return so token statistics flow through to `DailyStatsActor`. ([#367](https://github.com/Aaronontheweb/netclaw/pull/367))

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
