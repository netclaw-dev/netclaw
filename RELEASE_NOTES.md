# NetClaw Release Notes

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

## 0.25.0-beta.3 (2026-07-12)

### Features
- **Discord DM reminder delivery** — Reminders can now be delivered to Discord DMs via improved `DiscordReminderTargetResolver` ([#1609](https://github.com/netclaw-dev/netclaw/pull/1609))
- **Named model configuration & provider runtime validation** — New `NamedModelConfiguration` and `ProviderRuntimeValidation` types, config schema updates, and CLI wizard improvements for provider/model setup ([#1610](https://github.com/netclaw-dev/netclaw/pull/1610))

### Bug Fixes
- **Model set/picker preserves hand-set modalities** — Re-selecting the same model no longer wipes operator-set `InputModalities`/`OutputModalities` and `ContextWindow`. Added `--input-modalities`, `--output-modalities`, `--clear-modalities`, and `--clear-context-window` CLI flags ([#1610](https://github.com/netclaw-dev/netclaw/pull/1610))
- **Slack processing status serialization** — Slack processing status updates are now serialized to prevent race conditions during concurrent sends ([#1556](https://github.com/netclaw-dev/netclaw/pull/1556))
- **Sub-agent token usage tracked in daily stats** — Sub-agent LLM calls now record token usage, making them visible in `netclaw stats` ([#1597](https://github.com/netclaw-dev/netclaw/pull/1597))
- **Subagents fail closed for unattended approvals** — When a subagent requires approval but the session is unattended, it now fails closed instead of proceeding or hanging ([#1616](https://github.com/netclaw-dev/netclaw/pull/1616))

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