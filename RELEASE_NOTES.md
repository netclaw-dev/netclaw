# NetClaw Release Notes

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