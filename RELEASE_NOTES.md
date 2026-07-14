# NetClaw Release Notes

## 0.25.0-alpha.onnx.6 (2026-07-14)

> Experimental ONNX local-embeddings build. Syncs `feature/memory-embeddings` with `dev`
> through 0.25.0-beta.4. No changes to memory/embeddings behavior in this release — this is
> a mainline sync only. Everything still rides `Memory.Embeddings.Enabled`, off by default;
> install only by exact pin (`NETCLAW_VERSION=0.25.0-alpha.onnx.6`).

### Features
- **Preserve Git working context across sessions and subagents** — a bounded, audience-aware Git working-context snapshot stays current in the system prompt, and coding subagents inherit recent-file/project context so parent sessions merge back only confirmed successful child edits ([#1630](https://github.com/netclaw-dev/netclaw/pull/1630))

### Bug Fixes
- **Curation dedup no longer overwrites existing memories** — a Create-decision anchor collision now appends below a dated separator instead of silently replacing the document; verbatim duplicates are skipped. ([#1637](https://github.com/netclaw-dev/netclaw/pull/1637))
- **STDIO MCP server arguments no longer rewritten** — the daemon now preserves configured STDIO MCP server arguments, and uses one daemon-owned client per configured MCP server ([#1636](https://github.com/netclaw-dev/netclaw/pull/1636))

### Improvements
- **Logical skill access and authoritative inventory refresh** — skill loading now resolves through logical `skill_load`/`skill_read_resource` access with native > managed-feed > external precedence ([#1634](https://github.com/netclaw-dev/netclaw/pull/1634))

## 0.25.0-beta.4 (2026-07-14)

### Features
- **Preserve Git working context across sessions and subagents** — A bounded, audience-aware Git working-context snapshot (branch, worktree, repository, upstream, changed files) now stays current in the system prompt, and coding subagents inherit recent-file/project context so parent sessions merge back only confirmed successful child edits. Measured 20% → 100% success rate on a linked-worktree coding eval ([#1630](https://github.com/netclaw-dev/netclaw/pull/1630))
- **User-written `AGENTS.md` for application-specific agent guidance** — Operators can now author `~/.netclaw/identity/AGENTS.md`, layered after Netclaw's embedded operating core and inherited by sub-agents, to give the running agent deployment-specific mission and workflow guidance. Seeded with a minimal scaffold during init without overwriting existing guidance ([#1622](https://github.com/netclaw-dev/netclaw/pull/1622))

### Bug Fixes
- **Memory curation no longer overwrites existing documents on collision** — Fixed: when a curation Create decision targeted an anchor that already had a document, the write silently overwrote the existing title, body, and classification with no history — observed 88 times in 14 days in production, including one case that destroyed an LLM-merged document. Collisions now append the new content under a dated separator instead of overwriting; a verbatim duplicate is skipped as a no-op ([#1637](https://github.com/netclaw-dev/netclaw/pull/1637))
- **STDIO MCP server arguments no longer rewritten** — Fixed: configured STDIO MCP server arguments were being rewritten by the daemon; the daemon now preserves them as configured. Also simplified to one daemon-owned client per configured MCP server, removing Playwright-specific session-scoped process handling ([#1636](https://github.com/netclaw-dev/netclaw/pull/1636))

### Improvements
- **Logical skill access and authoritative inventory refresh** — Skill loading now resolves through logical `skill_load`/`skill_read_resource` access instead of physical skill-root paths, with native > managed-feed > external precedence. Startup, sync, watcher, and `skill_manage` inventory rebuilds are now centralized through one live-source refresher publishing atomic registry snapshots ([#1634](https://github.com/netclaw-dev/netclaw/pull/1634))

### Dependency Updates
- **Bump SkillServer to stable** — `Netclaw.SkillClient` 0.4.0-beta.4 → 0.4.0 (stable release) ([#1638](https://github.com/netclaw-dev/netclaw/pull/1638))

## 0.25.0-alpha.onnx.5 (2026-07-12)

> **Experimental feature build** (fifth in the memory-embeddings series). This build carries
> **no changes to memory/embeddings behavior** versus `0.25.0-alpha.onnx.4` — it merges the
> mainline `dev` branch to bring the experimental line current with recent fixes, most
> importantly a fail-closed hardening of unattended sub-agent approvals. Same gating as before:
> everything rides `Memory.Embeddings.Enabled`, off by default; install only by exact pin
> (`NETCLAW_VERSION=0.25.0-alpha.onnx.5`). Upgrading from alpha.onnx.4 is a binary swap — no
> config, data, or unit changes.

### Security
- **Fail-closed unattended sub-agent approvals** — a sub-agent spawned from a session whose transport cannot service interactive approval prompts no longer inherits an approval bridge from the parent context; bridge presence alone can never make an unattended child interactive. Prevents an approval-gated tool from becoming silently auto-approvable in headless, webhook, and reminder-driven turns ([#1616](https://github.com/netclaw-dev/netclaw/pull/1616))

### Bug Fixes
- **Discord DM reminders** — reminders now fire correctly in Discord direct-message channels ([#1609](https://github.com/netclaw-dev/netclaw/pull/1609))
- **Slack processing-status updates serialized** — concurrent status updates on a Slack thread no longer race ([#1556](https://github.com/netclaw-dev/netclaw/pull/1556))
- **CLI model picker preserves hand-set modalities** — re-setting a model via `netclaw model set` or the picker no longer discards manually-configured input modalities ([#1610](https://github.com/netclaw-dev/netclaw/pull/1610))

### Dependency Updates
- ModelContextProtocol versioning consolidated into the central props file ([#1614](https://github.com/netclaw-dev/netclaw/pull/1614))
- MessagePack 3.1.7 → 3.1.8 ([#1605](https://github.com/netclaw-dev/netclaw/pull/1605))
- .NET SDK 10.0.300 → 10.0.301 ([#1381](https://github.com/netclaw-dev/netclaw/pull/1381))

## 0.25.0-beta.3 (2026-07-12)

### Features
- **Discord DM reminder delivery** — Reminders can now be delivered to Discord DMs via improved `DiscordReminderTargetResolver` ([#1609](https://github.com/netclaw-dev/netclaw/pull/1609))
- **Named model configuration & provider runtime validation** — New `NamedModelConfiguration` and `ProviderRuntimeValidation` types, config schema updates, and CLI wizard improvements for provider/model setup ([#1610](https://github.com/netclaw-dev/netclaw/pull/1610))

### Bug Fixes
- **Model set/picker preserves hand-set modalities** — Re-selecting the same model no longer wipes operator-set `InputModalities`/`OutputModalities` and `ContextWindow`. Added `--input-modalities`, `--output-modalities`, `--clear-modalities`, and `--clear-context-window` CLI flags ([#1610](https://github.com/netclaw-dev/netclaw/pull/1610))
- **Slack processing status serialization** — Slack processing status updates are now serialized to prevent race conditions during concurrent sends ([#1556](https://github.com/netclaw-dev/netclaw/pull/1556))
- **Sub-agent token usage tracked in daily stats** — Sub-agent LLM calls now record token usage, making them visible in `netclaw stats` ([#1597](https://github.com/netclaw-dev/netclaw/pull/1597))
- **Subagents fail closed for unattended approvals** — When a subagent requires approval but the session is unattended, it now fails closed instead of proceeding or hanging ([#1616](https://github.com/netclaw-dev/netclaw/pull/1616))

## 0.25.0-alpha.onnx.4 (2026-07-09)

> **Experimental feature build** (fourth in the memory-embeddings series). Same gating:
> everything rides `Memory.Embeddings.Enabled`, off by default; install only by exact pin
> (`NETCLAW_VERSION=0.25.0-alpha.onnx.4`). **Upgrade note:** the systemd unit template
> changed — after installing, re-run `netclaw daemon install` to regenerate the unit and
> pick up the new graceful-shutdown settings.

### Features
- **Operational alert on model provisioning failure** — if either ONNX model (embedder or relevance reranker) cannot be downloaded, verified, or loaded while embeddings are enabled, the daemon now pushes an operational alert to configured notification targets (same channel as reminder-failure alerts) with the failure reason and remediation, once per model per daemon run — semantic-memory degradation is no longer discoverable only via doctor/logs ([#1611](https://github.com/netclaw-dev/netclaw/pull/1611))

### Bug Fixes
- **Embedder failure no longer blocks the relevance model** — a provisioning failure in the embedding model made the reranker's provisioning unreachable, silently disabling the relevance gate alongside it ([#1611](https://github.com/netclaw-dev/netclaw/pull/1611))
- **Graceful daemon shutdown** — `netclaw daemon stop` self-escalated to SIGKILL after 10s while the daemon's own shutdown budget allows 200s to drain in-flight turns; one `GracefulShutdownBudget` now governs the Akka shutdown phase, host shutdown timeout, CLI wait, and the generated unit's `TimeoutStopSec` ([#1612](https://github.com/netclaw-dev/netclaw/pull/1612))
- **`--help` no longer executes commands** — `netclaw memory backfill-embeddings --help` ran a real backfill; worse, `netclaw daemon stop --help` actually stopped the daemon. Trailing help tokens are now handled uniformly across memory, daemon, webhooks, and reminder subcommands ([#1612](https://github.com/netclaw-dev/netclaw/pull/1612))

## 0.25.0-alpha.onnx.3 (2026-07-09)

> **Experimental feature build** (third in the memory-embeddings series) — the canary-feedback
> batch: fixes found by running 0.25.0-alpha.onnx.2 in production. Same gating as before:
> everything rides `Memory.Embeddings.Enabled`, off by default; install only by exact pin
> (`NETCLAW_VERSION=0.25.0-alpha.onnx.3`). Upgrading from alpha.onnx.2 is a binary swap —
> no config or data changes; models re-verify from disk without re-downloading.

### Bug Fixes
- **Relevance-gate cold starts** — after idle periods the whole recall pipeline could exceed its 300ms envelope before the cross-encoder gate ever ran (paged-out ONNX sessions + host contention), silently skipping the gate. Fixed with periodic keep-warm inference on both models, an envelope-derived gate sub-budget (120ms ceiling, clamped to remaining turn budget), and per-turn `gateElapsedMs` observability ([#1608](https://github.com/netclaw-dev/netclaw/pull/1608))
- **`netclaw memory` command not dispatchable** — the command was advertised in help and fully implemented but missing from the CLI parser's known-command set; `backfill-embeddings` was unusable. Fixed, with a bidirectional sync test deriving ground truth from the dispatch source so the parser/handler/help trio cannot drift again
- **`netclawd --version` booted the daemon** — the daemon binary ignored the flag and started a real instance; now prints the version and exits without touching directories or the daemon lock
- **Version banners show the full version** — `--version` in both binaries previously printed the truncated numeric version (`0.25.0`), hiding the prerelease suffix; both now print the full semver

## 0.25.0-alpha.onnx.2 (2026-07-08)

> **Experimental feature build** (second in the memory-embeddings series). Everything here
> is gated behind `Memory.Embeddings.Enabled`, **off by default** — without opting in,
> behavior is identical to the mainline beta. Not published to the beta channel; install
> only by exact pin: `NETCLAW_VERSION=0.25.0-alpha.onnx.2`.

### Memory (Experimental)
- **Hybrid semantic recall with an absolute relevance floor** — automatic pre-turn recall now unions FTS5 lexical and embedding-cosine candidates (identical policy gates for both), fuses scores with recency decay, and enforces a gold-set-calibrated minimum-similarity floor: when nothing relevant exists, nothing is injected. Zero-injection turns are normal and healthy.
- **Cross-encoder relevance gate** — a 22 MB int8 reranker (ms-marco-MiniLM-L-6-v2, hash-pinned) scores each floor survivor against the query and drops weak matches; measured out-of-sample at 86.8% zero-injection accuracy with 98.3% recall retention. Follows `Memory.Embeddings.Enabled`; degraded mode falls back to floor-only recall, never blocks a turn.
- **Model-documented query prefix + manifest-carried calibration** — recall queries now embed in arctic-embed's documented retrieval mode; each allowlisted model pins its prefix and calibrated floor together, and `Memory.Recall.MinCosineSimilarity` follows the active model's calibration unless explicitly overridden. Measured on the production gold set: F0.5 +73%, recall@3 2.8×, zero-injection accuracy 2.1× vs the unprefixed configuration.
- **int8 arctic embedder is the new default model** — Snowflake's pre-quantized `model_uint8.onnx` (105 MB vs 416 MB fp32, ~1.7× faster, measurably better retrieval quality with the prefix). fp32 and mxbai remain allowlisted as explicit choices.

### Upgrading from 0.25.0-alpha.onnx.1 with embeddings enabled
- The default model id changes to `snowflake-arctic-embed-m-int8`. On first daemon start the warmup gap-repair sweep re-embeds your corpus under the new model automatically (recall degrades to lexical-only for unembedded documents until coverage completes); `netclaw memory backfill-embeddings --force` does it in one pass. Existing fp32 vectors are left in place and untouched; `netclaw doctor` will note the mixed-model rows until you re-backfill. Original memory content is never modified.

## 0.25.0-alpha.onnx.1 (2026-07-08)

> **Experimental feature build.** This is a named experimental prerelease of the semantic
> memory-embeddings foundation, gated behind a config flag that is **off by default**
> (`Memory.Embeddings.Enabled`). Without opting in, runtime behavior is identical to
> 0.25.0-beta.2, which this build fully contains. It is not published to the beta channel —
> install only by exact pin: `NETCLAW_VERSION=0.25.0-alpha.onnx.1`. Embeddings live in a
> new additive table; disabling the flag or downgrading afterward is safe — vectors are
> derived data and original memory content is never touched.

### Memory (Experimental)
- **Semantic memory embeddings (opt-in)** — In-process ONNX embedding runtime (snowflake-arctic-embed-m, CPU-only, hash-pinned model downloaded at daemon startup), embed-on-write with startup gap repair, a `netclaw memory backfill-embeddings [--force]` CLI command, and a doctor check for model/coverage status ([#1577](https://github.com/netclaw-dev/netclaw/pull/1577))
- **Semantic dedup: kNN-nominate / LLM-decide with lossless merges** — With embeddings enabled, near-duplicate memories are nominated by vector similarity and merged by the curation LLM under a deterministic MergeGuard; guard failures fall back to a lossless structural append ([#1585](https://github.com/netclaw-dev/netclaw/pull/1585))
- **Guard-rejected anchor updates fall through to nomination** — instead of being silently dropped ([#1587](https://github.com/netclaw-dev/netclaw/pull/1587))

## 0.25.0-beta.2 (2026-07-07)

### Bug Fixes
- **UTF-8 BOM in skill frontmatter** — Fixed: skill scanner now strips UTF-8 BOM (`\uFEFF`) before parsing YAML frontmatter, and populates `SkillName` on all `SkillScanIssue` records so degenerate frontmatter no longer crashes the scan ([#1583](https://github.com/netclaw-dev/netclaw/pull/1583))
- **Model capability provenance logging** — Fixed: daemon now logs effective model capabilities with their provenance source, improving diagnostic visibility for model configuration issues ([#1584](https://github.com/netclaw-dev/netclaw/pull/1584))

### Dependency Updates
- **Bump SkillServer** — `Netclaw.SkillClient` 0.4.0-beta.1 → 0.4.0-beta.3 and adapt to API changes ([#1593](https://github.com/netclaw-dev/netclaw/pull/1593))

## 0.25.0-beta.1 (2026-07-05)

### Features
- **SkillServer native sub-agent sync** — Optional native manifest sidecar sync for server-managed sub-agents, keeping RFC skill sync primary while downloading verified native artifacts when available. Local sub-agent files load before server-feed files so user-authored definitions always win ([#1539](https://github.com/netclaw-dev/netclaw/pull/1539))

### Bug Fixes
- **Systemd shell tool PATH** — Fixed: daemon now captures the operator's full PATH into the systemd EnvironmentFile instead of relying on a hardcoded list, so tools like `~/.dotnet/dotnet` are visible to the shell tool ([#1565](https://github.com/netclaw-dev/netclaw/pull/1565))

### Memory
- **Shared curation evaluator** — Unified curation logic across both memory write pipelines (inline per-session actor and daemon checkpoint-worker) so they can never diverge again ([#1575](https://github.com/netclaw-dev/netclaw/pull/1575))
- **Memory audit quick wins** — July 2026 audit: revived curation LLM, balanced prompt, and recall precision re-tune ([#1568](https://github.com/netclaw-dev/netclaw/pull/1568))

## 0.24.4 (2026-07-03)

### Bug Fixes
- **Environment-variable-only configuration** — Fixed doctor misdiagnosis and OAuth config-file side effect that prevented daemon startup when configuration is supplied entirely via environment variables ([#1569](https://github.com/netclaw-dev/netclaw/pull/1569))
- **Remote daemon CLI** — Fixed: CLI no longer demands a local daemon config file when the daemon is explicitly configured as remote ([#1567](https://github.com/netclaw-dev/netclaw/pull/1567))

### Dependency Updates
- **Bump Anthropic SDK** — 12.34.1 → 12.35.1 ([#1561](https://github.com/netclaw-dev/netclaw/pull/1561))
- **Bump Akka group** — Akka.Cluster.Sharding and Akka.Persistence 1.5.69 → 1.5.70 ([#1560](https://github.com/netclaw-dev/netclaw/pull/1560))

## 0.24.3 (2026-07-03)

### Features
- **GitHub Enterprise Copilot support** — Authenticate GitHub Copilot tokens against GHE instances and route requests to the correct data residency endpoint ([#1509](https://github.com/netclaw-dev/netclaw/pull/1509), [#1512](https://github.com/netclaw-dev/netclaw/pull/1512), [#1555](https://github.com/netclaw-dev/netclaw/pull/1555))
- **Slack native processing status** — Real-time processing indicators in Slack instead of generic "working" messages ([#1524](https://github.com/netclaw-dev/netclaw/pull/1524))
- **Reminder failure visibility** — Operators can now see when reminders fail or are skipped ([#1503](https://github.com/netclaw-dev/netclaw/pull/1503))
- **Degraded startup mode** — Daemon starts with a "no valid model" banner when no provider is configured, instead of failing host startup. Removed silent local-ollama default ([#1540](https://github.com/netclaw-dev/netclaw/pull/1540))
- **Synced skill resources via shell** — Skill resources synced from the cloud can now execute via shell commands ([#1551](https://github.com/netclaw-dev/netclaw/pull/1551))

### Bug Fixes
- **Autonomous sessions can write workspace directory** — Fixed: autonomous sessions could not write to the workspace directory ([#1498](https://github.com/netclaw-dev/netclaw/pull/1498))
- **Reminder duplicate-execution guard** — Fixed: Mode A reminder session wedge prevented the duplicate guard from releasing ([#1500](https://github.com/netclaw-dev/netclaw/pull/1500))
- **Reset stops daemon first** — Fixed: `netclaw reset` now stops the daemon before proceeding and shows a progress screen ([#1494](https://github.com/netclaw-dev/netclaw/pull/1494))
- **MCP tool errors display cleanly** — Fixed: MCP errors now show as attributed messages instead of raw JSON dumps ([#1510](https://github.com/netclaw-dev/netclaw/pull/1510))
- **TUI identity redo timezone loop** — Fixed: identity redo would loop on timezone; also fixed session browser regression ([#1518](https://github.com/netclaw-dev/netclaw/pull/1518))
- **Subagent terminal result summaries** — Fixed: subagent terminal results not displaying properly ([#1519](https://github.com/netclaw-dev/netclaw/pull/1519))
- **Flaky host crash during install reset** — Fixed: thread-unsafe ReactiveProperty access during reset caused crashes ([#1525](https://github.com/netclaw-dev/netclaw/pull/1525))
- **Session browser selection highlight** — Fixed: session browser didn't show selection highlight in TUI ([#1531](https://github.com/netclaw-dev/netclaw/pull/1531))
- **Slack active thread status refresh** — Fixed: thread status not refreshing during tool loops and buffered messages ([#1534](https://github.com/netclaw-dev/netclaw/pull/1534))
- **Stable memory handles** — Fixed: memory handles were unstable, affecting cross-session memory ([#1538](https://github.com/netclaw-dev/netclaw/pull/1538))
- **GHE Copilot routing** — Fixed: GHE Copilot chat now routes to the token's `endpoints.api` host instead of hardcoded `api.githubcopilot.com` ([#1555](https://github.com/netclaw-dev/netclaw/pull/1555))
- **Security: Microsoft.OpenApi CVE-2026-49451** — Pinned to 2.7.5 to address vulnerability ([#1543](https://github.com/netclaw-dev/netclaw/pull/1543))

### Breaking Changes
- **Removed silent local-ollama fallback** — Users without any provider configured will now see a "no valid model" banner instead of an automatic fallback to local Ollama. Explicit provider configuration is now required for full functionality ([#1540](https://github.com/netclaw-dev/netclaw/pull/1540))

### Performance
- **Log stream partitioned by session** — Each session now gets its own `session.log` while `daemon.log` remains sparse. Cleaner logs and better observability with OTEL union support ([#1499](https://github.com/netclaw-dev/netclaw/pull/1499))