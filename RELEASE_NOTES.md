#### 0.24.2 2026-06-25 ####

Netclaw v0.24.2 — DwarfStar provider support, sub-agent operating rule inheritance, systemd update reliability, and reliability improvements

**Features**

* **DwarfStar (ds4) provider support** — Added DwarfStar as a supported provider via the OpenAI-compatible backend strategy. ([#1349](https://github.com/netclaw-dev/netclaw/pull/1349))

* **Sub-agents inherit embedded AGENTS.md operating rules** — Sub-agents spawned by a parent session now inherit the embedded `AGENTS.md` operating rules from the parent's identity context, ensuring consistent operating constraints across the agent hierarchy. ([#1490](https://github.com/netclaw-dev/netclaw/pull/1490))

* **Schema-aware meta argument coercion** — ChatGPT-trained models (Qwen3.6, etc.) were being hard-rejected for using non-canonical meta argument names (`TimeoutSeconds` vs `_timeout_seconds`). This adds schema-aware near-miss resolution that coercively maps model-supplied key variants to canonical names when no real tool parameter shadows them. Fixes "tool was NOT executed" errors that pushed models off tools and into sandbox-style output. ([#1470](https://github.com/netclaw-dev/netclaw/pull/1470))

* **Preserve per-call meta hints across tool-batch re-drive** — Tool calls awaiting approval that passivate and cold-recover were losing meta hints (`_timeout_seconds`, `_rationale`, `_background`), silently falling back to defaults. Long-running tools approved after session recovery now respect the original timeout. ([#1470](https://github.com/netclaw-dev/netclaw/pull/1470))

**Bug Fixes**

* **Subagents: fixed premature kill by parent watchdog during approval waits** — Sub-agents waiting for human approval were incorrectly killed by the parent session's inactivity watchdog. Sub-agents now manage their own liveness end-to-end and are no longer subject to parent-side timeouts. ([#1481](https://github.com/netclaw-dev/netclaw/pull/1481))

* **Subagents: honor approval-pause in every tool-watchdog mode** — In WallClock mode (applied to every opaque tool), the human-approval pause was ignored: the wall-clock budget kept ticking through a human approval, killing healthy tools mid-wait when humans took longer than the budget. Prevents premature tool termination during human-in-the-loop approvals. ([#1473](https://github.com/netclaw-dev/netclaw/pull/1473))

* **Subagents: record spawn lifecycle in the session transcript** — Sub-agent actor logs went through Akka's async logger bridge and were lost because `SessionDiagnosticsContext.AsyncLocal` was gone. Spawns that failed early (guard rejection, watchdog kill, child-actor failure) left the session transcript completely silent. Parent-side breadcrumbs now log spawn lifecycle under the parent session scope. ([#1468](https://github.com/netclaw-dev/netclaw/pull/1468))

* **Skills: block agent mutations to server-feed skill directories** — Agents could freely edit/delete/write files into `.server-feeds/` skill directories via both `skill_manage` and direct file-write tools. Changes were silently overwritten on the next sync cycle. Two enforcement layers added: `skill_manage` guard + `ToolPathPolicy` write-deny for server-feed paths. Prevents wasted agent effort and misleading session state from server-feed mutations. ([#1466](https://github.com/netclaw-dev/netclaw/pull/1466))

* **Providers: refresh inference OAuth tokens** — OpenAI Copilot and other inference providers with OAuth tokens now refresh tokens at runtime rather than relying on stale tokens. Includes Copilot probe-time token refresh. Prevents authentication failures during long-running sessions when tokens expire. ([#1465](https://github.com/netclaw-dev/netclaw/pull/1465))

* **`netclaw update` no longer crash-loops systemd on Linux** — On Linux hosts with Netclaw installed as a systemd `--user` service, `netclaw update` was restarting the daemon as a detached process (bypassing systemd). This left the still-enabled systemd unit in a permanent crash-loop — every 5 seconds it tried to start, failed because the detached daemon held the exclusive lock file, and retried indefinitely (restart counters in the tens of thousands). `netclaw update` now delegates start/stop to `systemctl --user` when an enabled user unit exists. ([#1469](https://github.com/netclaw-dev/netclaw/pull/1469))

**Performance**

* **Optimize `LlmSessionActor` storage** — Only retain the most recent snapshot + last N messages in the journal, instead of storing full history. Reduces memory consumption for long sessions with many messages. ([#1464](https://github.com/netclaw-dev/netclaw/pull/1464))

**Dependency Updates**

* **Bump MessagePack from 2.5.301 to 3.1.7** — Major version bump with serialization improvements. Please test your workflows after upgrading. ([#1420](https://github.com/netclaw-dev/netclaw/pull/1420))

* **Bump Termina from 0.14.0-beta3 to 0.14.0** — TUI framework promoted to stable; includes a fix for the MCP tool permissions page layout. ([#1480](https://github.com/netclaw-dev/netclaw/pull/1480), [#1483](https://github.com/netclaw-dev/netclaw/pull/1483))

* **Bump Anthropic SDK** — 12.29.1 → 12.30.0 (semver-minor). ([#1460](https://github.com/netclaw-dev/netclaw/pull/1460))

#### 0.24.2-beta.1 2026-06-24 ####

Netclaw v0.24.2-beta.1 — Schema-aware meta argument coercion, reliability fixes, and performance improvements

**Features**

* **Schema-aware meta argument coercion** — ChatGPT-trained models (Qwen3.6, etc.) were being hard-rejected for using non-canonical meta argument names (`TimeoutSeconds` vs `_timeout_seconds`). This adds schema-aware near-miss resolution that coercively maps model-supplied key variants to canonical names when no real tool parameter shadows them. Fixes "tool was NOT executed" errors that pushed models off tools and into sandbox-style output. ([#1470](https://github.com/netclaw-dev/netclaw/pull/1470))

* **Preserve per-call meta hints across tool-batch re-drive** — Tool calls awaiting approval that passivate and cold-recover were losing meta hints (`_timeout_seconds`, `_rationale`, `_background`), silently falling back to defaults. Fixes a pre-existing bug where the re-drive path never reinjected persisted MetaJson. Long-running tools approved after session recovery now respect the original timeout. ([#1470](https://github.com/netclaw-dev/netclaw/pull/1470))

**Bug Fixes**

* **Subagents: honor approval-pause in every tool-watchdog mode** — In WallClock mode (applied to every opaque tool), the human-approval pause was ignored: the wall-clock budget kept ticking through a human approval, killing healthy tools mid-wait when humans took longer than the budget. Prevents premature tool termination during human-in-the-loop approvals. ([#1473](https://github.com/netclaw-dev/netclaw/pull/1473))

* **Skills: block agent mutations to server-feed skill directories** — Agents could freely edit/delete/write files into `.server-feeds/` skill directories via both `skill_manage` and direct file-write tools. Changes were silently overwritten on the next sync cycle. Two enforcement layers added: `skill_manage` guard + `ToolPathPolicy` write-deny for server-feed paths. Prevents wasted agent effort and misleading session state from server-feed mutations. ([#1466](https://github.com/netclaw-dev/netclaw/pull/1466))

* **Subagents: record spawn lifecycle in the session transcript** — Sub-agent actor logs went through Akka's async logger bridge and were lost because `SessionDiagnosticsContext.AsyncLocal` was gone. Spawns that failed early (guard rejection, watchdog kill, child-actor failure) left the session transcript completely silent. Parent-side breadcrumbs now log spawn lifecycle under the parent session scope. ([#1468](https://github.com/netclaw-dev/netclaw/pull/1468))

* **Providers: refresh inference OAuth tokens** — OpenAI Copilot and other inference providers with OAuth tokens now refresh tokens at runtime rather than relying on stale tokens. Includes Copilot probe-time token refresh. Prevents authentication failures during long-running sessions when tokens expire. ([#1465](https://github.com/netclaw-dev/netclaw/pull/1465))

**Performance**

* **Optimize `LlmSessionActor` storage** — Only retain the most recent snapshot + last N messages in the journal, instead of storing full history. Reduces memory consumption for long sessions with many messages. ([#1464](https://github.com/netclaw-dev/netclaw/pull/1464))

**Dependency Updates**

* **Bump MessagePack from 2.5.301 to 3.1.7** — Major version bump with serialization improvements. Please test your workflows after upgrading. ([#1420](https://github.com/netclaw-dev/netclaw/pull/1420))

* **Bump Anthropic SDK** — 12.29.1 → 12.30.0 (semver-minor). ([#1460](https://github.com/netclaw-dev/netclaw/pull/1460))

* **Bump Aspire.Hosting.Testing** — 13.4.4 → 13.4.6 (semver-patch). ([#1462](https://github.com/netclaw-dev/netclaw/pull/1462))

#### 0.24.1 2026-06-23 ####

Netclaw v0.24.1 — TUI reliability improvements, remote skill server awareness, spawn_agent liveness fixes, and SQLitePCLRaw CVE mitigation

**Features**

* **Show advertised skill count for remote skill servers** — the TUI now displays the number of skills advertised by each remote skill server, giving users visibility into available capabilities. ([#1452](https://github.com/netclaw-dev/netclaw/pull/1452))

**Bug Fixes**

* **SQLitePCLRaw CVE suppression** — suppressed GHSA-2m69-gcr7-jv3q (SQLitePCLRaw CVE-2025-6965) until upstream patches the vulnerability. ([#1444](https://github.com/netclaw-dev/netclaw/pull/1444))

* **Auto-start init health checks** — the TUI now automatically starts health checks during the init wizard, ensuring servers are ready before proceeding. ([#1454](https://github.com/netclaw-dev/netclaw/pull/1454))

* **Config-screen consistency** — the TUI config screen now consistently shows Done rows, an embedded footer, and proper Search spacer styling. ([#1441](https://github.com/netclaw-dev/netclaw/pull/1441))

* **Auto-advance add-skill-server flow** — the skill server add flow now automatically advances on a successful probe, reducing manual steps. ([#1458](https://github.com/netclaw-dev/netclaw/pull/1458))

* **spawn_agent liveness respect** — spawn_agent now properly respects self-monitoring liveness checks, ensuring child agents are health-checked correctly after startup. ([#1453](https://github.com/netclaw-dev/netclaw/pull/1453))

* **Security & Access menu Done rows** — added Done rows to the Security & Access menu and editors for consistent UI feedback. ([#1448](https://github.com/netclaw-dev/netclaw/pull/1448))


#### 0.24.0 2026-06-18 ####

Netclaw v0.24.0 — Beta release channel, streaming-native chat stack, rebuilt config TUI, Docker reliability, and the full 0.24.0 beta journey

**Major Features**

* **Opt-in beta release channel** — Netclaw can now publish and install prereleases without touching a default install. A new `latestPrerelease` pointer (newest of all releases) works alongside `latest` (newest stable); `--channel beta` / `-Channel beta` and the rolling `:beta` Docker tag resolve to it. Stable clients only ever read `latest`. ([#1314](https://github.com/netclaw-dev/netclaw/pull/1314))

* **Channel-aware, SemVer-correct update check** — the binary update check honors `Daemon.UpdateChannel` (`stable` default, or `beta`) and compares versions by SemVer 2.0.0 precedence. Beta testers are notified of the next prerelease and automatically roll onto stable once it supersedes; stable users are never offered a prerelease. ([#1315](https://github.com/netclaw-dev/netclaw/pull/1315))

* **Streaming-native chat-client stack with composable routing** — the `IChatClient` stack has been redesigned so the streaming-vs-non-streaming transport distinction no longer leaks into callers. Netclaw now issues only streaming requests across all 8 providers, eliminating the OpenAI Codex `400 "Stream must be set to true"` error and preventing reasoning content drops. Resilience and observability are compositional via `Microsoft.Extensions.AI.ChatClientBuilder` — each (provider, model) pipeline is assembled as `Logging → Retry → VendorOptions → raw`. A `RoutingChatClient` walks ordered candidate lists for failover, and `LoggingChatClient` is now stateless with session-correlated log tags (`SessionId`). ([#1313](https://github.com/netclaw-dev/netclaw/pull/1313))

* **Rebuilt config TUI and simplified init wizard** — the configuration TUI has been rebuilt from scratch with a streamlined init wizard, canonical channel-ID resolution for all gateway integrations, scrollable list views, and native text selection via Termina. ([#1368](https://github.com/netclaw-dev/netclaw/pull/1368), [#1359](https://github.com/netclaw-dev/netclaw/pull/1359), [#1363](https://github.com/netclaw-dev/netclaw/pull/1363))

* **Background jobs as detached processes** — background jobs now run as detached processes with live log streaming, no default kill timer, and automatic reaping on passivation. ([#1405](https://github.com/netclaw-dev/netclaw/pull/1405))

* **Shell streaming support** — `shell_execute` output now streams incrementally instead of waiting for full completion, fixing the hard 90s timeout for long-running commands. ([#1360](https://github.com/netclaw-dev/netclaw/pull/1360))

* **Standardized channel infrastructure (SPEC-015)** — generic `ChannelLifecycleActor` and `RemoteChatChannelBuilder` reduce new channel implementations to ~80 LOC (down from 1,100+ duplicated LOC across Discord and Mattermost), while enforcing a standardized security pipeline and gateway lifecycle. ([#1375](https://github.com/netclaw-dev/netclaw/pull/1375))

* **Bounded tool output with file spill** — large tool outputs are now bounded and spilled to a file instead of flooding the session, keeping context lean while preserving the full output on disk. ([#1305](https://github.com/netclaw-dev/netclaw/pull/1305))

* **Loud tool-argument validation** — eliminated silent discard/degradation of LLM tool arguments; invalid arguments are now surfaced explicitly instead of being silently dropped. ([#1398](https://github.com/netclaw-dev/netclaw/pull/1398))

* **Channel delivery descriptor registry** — new registration-based system for channel delivery descriptors, improving extensibility of channel integrations. ([#1326](https://github.com/netclaw-dev/netclaw/pull/1326))

* **`lookup_channel_destination` blank-query support** — passing `query: null` or an empty string now returns all available destinations, enabling "Select Destination" TUI steps. ([#1375](https://github.com/netclaw-dev/netclaw/pull/1375))

**Bug Fixes**

* **Discord gateway no longer enters zombie state after failed auto-retry** — fixed a critical reliability issue where the Discord gateway dropped every inbound message for 30+ minutes and would not recover without a daemon restart. ([#1374](https://github.com/netclaw-dev/netclaw/pull/1374))

* **Mattermost auto-retry recovery publishes `ConnectionRestored`** — the same gateway-lifecycle fix applied to the Mattermost actor. ([#1375](https://github.com/netclaw-dev/netclaw/pull/1375))

* **Scheduler race in BackgroundJobManagerActor startup eliminated** — fixed a race condition in the scheduler's startup reconciliation logic that could cause jobs to be missed or duplicated during daemon initialization. ([#1417](https://github.com/netclaw-dev/netclaw/pull/1417))

* **In-session reminder delivery now confirms successfully** — fixed current-session reminders that were incorrectly reporting delivery failures. ([#1387](https://github.com/netclaw-dev/netclaw/pull/1387))

* **Reminder list includes disabled reminders** — the reminder list endpoint now correctly returns disabled reminders alongside active ones. ([#1386](https://github.com/netclaw-dev/netclaw/pull/1386))

* **Shell approval no longer matches bare integers or version/value arguments** — fixed false-positive pattern matches on numeric arguments and normalized version/value handling in approval verb chains. ([#1331](https://github.com/netclaw-dev/netclaw/pull/1331), [#1388](https://github.com/netclaw-dev/netclaw/pull/1388))

* **Bound per-turn empty/thinking-only response loops** — prevents agents from getting stuck in infinite loops of empty or thinking-only responses. ([#1358](https://github.com/netclaw-dev/netclaw/pull/1358))

* **Sub-agent logs are now session-correlated** — sub-agent log output is properly tagged with session context, making it easier to trace sub-agent activity. ([#1428](https://github.com/netclaw-dev/netclaw/pull/1428))

* **MCP fixes** — permission render clipping resolved; OAuth discovery skipped when static Authorization header is configured. ([#1424](https://github.com/netclaw-dev/netclaw/pull/1424), [#1357](https://github.com/netclaw-dev/netclaw/pull/1357))

* **Bounded image egress with dynamic resizing and caching** — SkiaSharp-backed `IImageNormalizer` downscales images to bounded dimensions (~1568px long-edge cap) and a base64 byte budget (~5MB) with two seams: normalize-at-ingestion for chat attachments and downscale-at-egress with content-hash cache for `file_read` images. Fails loud (drop-with-note, never raw passthrough), preventing OOM on large images. Configurable caps with schema sync. ([#1345](https://github.com/netclaw-dev/netclaw/pull/1345))

* **Secret placeholder writeback prevented** — file read/write tools no longer write back secret placeholders. ([#1343](https://github.com/netclaw-dev/netclaw/pull/1343))

* **Model manager manual entry state reset** — fixed stale manual entry state during model selection. ([#1344](https://github.com/netclaw-dev/netclaw/pull/1344))

* **Provider modality probing fixed** — Netclaw no longer persists guessed modalities; model-probe timeout and visibility issues resolved. ([#1311](https://github.com/netclaw-dev/netclaw/pull/1311))

* **OpenAI Codex calls no longer hang** — non-streaming Codex calls are served via streaming under the hood. ([#1289](https://github.com/netclaw-dev/netclaw/pull/1289))

* **Zero context-window models ignored** — models reporting zero context window are now ignored instead of breaking model selection. ([#1285](https://github.com/netclaw-dev/netclaw/pull/1285))

* **Init wizard readiness race fixed** — init readiness is gated on a daemon restart generation and a re-resolved endpoint. ([#1307](https://github.com/netclaw-dev/netclaw/pull/1307))

* **Standardized self-animating spinner across probe surfaces** — replaced five hand-rolled spinners with Termina's shared `SpinnerNode`; fixes frozen and slow spinners. ([#1312](https://github.com/netclaw-dev/netclaw/pull/1312), [#1327](https://github.com/netclaw-dev/netclaw/pull/1327))

* **TUI approval detail toggle remapped** — `Ctrl+V` → `Ctrl+O`, freeing up `Ctrl+V` for its expected use. ([#1362](https://github.com/netclaw-dev/netclaw/pull/1362))

* **DaemonApi threaded into init wizard's provider step** — init wizard's provider step now properly uses the DaemonApi. ([#1369](https://github.com/netclaw-dev/netclaw/pull/1369))

* **Flaky actor-startup tests fixed** — deterministic readiness barriers replace timing assumptions. ([#1410](https://github.com/netclaw-dev/netclaw/pull/1410), [#1378](https://github.com/netclaw-dev/netclaw/pull/1378))

* **Removed skills pruned from server feeds** — deleted skills no longer appear in the server's skill feed. ([#1408](https://github.com/netclaw-dev/netclaw/pull/1408))

* **Approval patterns terminate at multi-line arguments** — multi-line shell arguments are properly terminated with a summary in the display text. ([#1407](https://github.com/netclaw-dev/netclaw/pull/1407))

* **Install scripts persist `--channel` preference** — `Daemon.UpdateChannel` is now written to `netclaw.json` during `--channel beta` installs, so the daemon no longer silently defaults to stable. ([#1377](https://github.com/netclaw-dev/netclaw/pull/1377))

* **Lighter daemon memory footprint** — `netclawd` now uses Workstation GC. ([#1295](https://github.com/netclaw-dev/netclaw/pull/1295))

* **Windows installer uses User-scope PATH** — `netclaw` is found in new shells. ([#1274](https://github.com/netclaw-dev/netclaw/pull/1274))

* **Corrected version split in Directory.Build.props** — version parsing handles all version strings correctly. ([#1339](https://github.com/netclaw-dev/netclaw/pull/1339))

**Docker Improvements**

* **Self-dropping CLI launcher for root exec** — `/usr/local/bin/netclaw` transparently re-execs as the `netclaw` user when invoked as root, so `docker exec`/`kubectl exec` works without `gosu`/`-u`. ([#1322](https://github.com/netclaw-dev/netclaw/pull/1322))

* **Non-root agent can install tools at runtime in Docker** — user-writable, on-`PATH` install locations shipped so runtime-installed tools resolve as bare commands. ([#1321](https://github.com/netclaw-dev/netclaw/pull/1321))

* **Docker no longer crash-loops on read-only `/tools` mount** — treats `/tools` as best-effort. ([#1321](https://github.com/netclaw-dev/netclaw/pull/1321))

* **Docker reaps orphaned subprocesses** — uses `tini` to reap zombies. ([#1306](https://github.com/netclaw-dev/netclaw/pull/1306))

* **Docker owns the daemon lifecycle** — fixes conflicting restart behavior. ([#1282](https://github.com/netclaw-dev/netclaw/pull/1282))

* **Docker bind-mount ownership repaired** — mounted data is writable on startup. ([#1281](https://github.com/netclaw-dev/netclaw/pull/1281))

* **Docker root-drop log cleanup** — removed noisy log output when running as root. ([#1342](https://github.com/netclaw-dev/netclaw/pull/1342))

**Shell Reliability**

* **Shell pipe reads bounded** — bounded to `MaxOutputChars` before truncating, preventing runaway memory. ([#1298](https://github.com/netclaw-dev/netclaw/pull/1298))

* **Shell verifies the working directory** — confirms the working directory exists before launching, surfacing a clear error. ([#1299](https://github.com/netclaw-dev/netclaw/pull/1299))

**Dependency Updates**

* **Anthropic** 12.24.1 → 12.29.1 (#1352, #1412)
* **Termina** 0.10.2 → 0.12.1 (#1354, #1393)
* **Discord.Net** 3.19.1 → 3.20.1 (#1353)
* **Aspire.Hosting.AppHost** 13.4.2 → 13.4.4 (#1318, #1366, #1413)
* **Aspire.Hosting.Testing** 13.4.2 → 13.4.3 (#1366)
* **ModelContextProtocol.Core & ModelContextProtocol.AspNetCore** 1.4.0 (#1329, #1330)
* **CommunityToolkit.Aspire.Hosting.Ollama** 13.4.0 (#1320)
* **Grpc.Tools** 2.81.0 → 2.81.1 (#1269, #1400)
* **Verify.XunitV3** 31.19.0 → 31.19.1 (#1270, #1367)
* **Microsoft.AspNetCore.DataProtection** 10.0.8 → 10.0.9 (#1404)
* **Google.Protobuf** 3.35.0 → 3.35.1 (#1399)
* **OpenTelemetry** 1.15.3 → 1.16.0 (#1392)
* **slopwatch.cmd** 0.4.1 → 0.4.2 (#1421)
* **Akka group** 2 updates (#1411)

**Documentation & Internal**

* Documented beta/stable release process (#1323)
* Cited #648 at the chat-client routing seam (#1335)
* Archived completed OpenSpec changes (#1325, #1380, #1389)
* Added `.claude/worktrees` to `.gitignore` (#1336)
