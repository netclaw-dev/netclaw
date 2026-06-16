#### 0.24.0-beta.5 2026-06-16 ####

Netclaw v0.24.0-beta.5 — Background job streaming, tool argument validation, and dependency updates

**Features**

* **Background jobs as detached processes** — background jobs now run as detached processes with live log streaming, no default kill timer, and automatic reaping on passivation. ([#1405](https://github.com/netclaw-dev/netclaw/pull/1405))

* **Loud tool-argument validation** — eliminated silent discard/degradation of LLM tool arguments; invalid arguments are now surfaced explicitly instead of being silently dropped. ([#1398](https://github.com/netclaw-dev/netclaw/pull/1398))

**Bug Fixes**

* **Flaky actor-startup tests fixed with deterministic readiness barriers** — actor startup now uses deterministic readiness checks instead of relying on timing assumptions. ([#1410](https://github.com/netclaw-dev/netclaw/pull/1410), [#1378](https://github.com/netclaw-dev/netclaw/pull/1378))

* **Removed skills pruned from server feeds** — skills that have been deleted are now properly removed from the server's skill feed to avoid stale references. ([#1408](https://github.com/netclaw-dev/netclaw/pull/1408))

* **Approval patterns now terminate at multi-line arguments** — multi-line shell arguments are now properly terminated in approval matching, and the display text includes a summary of the matched patterns. ([#1407](https://github.com/netclaw-dev/netclaw/pull/1407))

**Dependencies**

* **Microsoft.AspNetCore.DataProtection bumped to 10.0.9** — security and bug fixes. ([#1404](https://github.com/netclaw-dev/netclaw/pull/1404))

* **Google.Protobuf bumped to 3.35.1** — protobuf runtime update. ([#1399](https://github.com/netclaw-dev/netclaw/pull/1399))

* **Grpc.Tools bumped to 2.81.1** — gRPC tooling update. ([#1400](https://github.com/netclaw-dev/netclaw/pull/1400))

* **OpenTelemetry bumped to 1.16.0** — observability runtime update. ([#1392](https://github.com/netclaw-dev/netclaw/pull/1392))

* **Termina bumped to 0.12.1** — terminal emulation updates. ([#1393](https://github.com/netclaw-dev/netclaw/pull/1393))

---

#### 0.24.0-beta.4 2026-06-11 ####

Netclaw v0.24.0-beta.4 — Reminder delivery fixes and shell approval normalization

**Bug Fixes**

* **In-session reminder delivery now confirms successfully** — fixed current-session reminders that were incorrectly reporting delivery failures. Reminders scheduled with `delivery_kind: current_session` now complete without spurious errors. ([#1387](https://github.com/netclaw-dev/netclaw/pull/1387))

* **Reminder list includes disabled reminders** — the reminder list endpoint now correctly returns disabled reminders alongside active ones, so you can see the full schedule even for paused reminders. ([#1386](https://github.com/netclaw-dev/netclaw/pull/1386))

* **Shell approval no longer matches version/value arguments** — normalized how version and value arguments are processed in shell approval verb chains, preventing false-positive pattern matches on numeric arguments. ([#1388](https://github.com/netclaw-dev/netclaw/pull/1388))

---

#### 0.24.0-beta.3 2026-06-10 ####

Netclaw v0.24.0-beta.3 — Channel infrastructure standardization, Discord/Mattermost gateway self-healing, and install script fix

**Features**

* **Standardized channel infrastructure (SPEC-015)** — generic `ChannelLifecycleActor` and `RemoteChatChannelBuilder` reduce new channel implementations to ~80 LOC (down from 1,100+ duplicated LOC across Discord and Mattermost), while enforcing a standardized security pipeline and gateway lifecycle. ([#1375](https://github.com/netclaw-dev/netclaw/pull/1375))

* **`lookup_channel_destination` blank-query support** — passing `query: null` or an empty string now returns all available destinations, enabling "Select Destination" TUI steps that list every channel without filtering. ([#1375](https://github.com/netclaw-dev/netclaw/pull/1375))

**Bug Fixes**

* **Discord gateway no longer enters zombie state after failed auto-retry** — fixed a critical reliability issue where the Discord gateway dropped every inbound message for 30+ minutes and would not recover without a daemon restart. The gateway now correctly enters its self-healing reconnect loop and publishes `ConnectionRestored` on recovery. ([#1374](https://github.com/netclaw-dev/netclaw/pull/1374))

* **Mattermost auto-retry recovery publishes `ConnectionRestored`** — the same gateway-lifecycle fix applied to the Mattermost actor; auto-retry timeouts now correctly trigger the self-healing reconnect loop. ([#1375](https://github.com/netclaw-dev/netclaw/pull/1375))

* **Install scripts persist `--channel` preference to config** — `Daemon.UpdateChannel` is now written to `netclaw.json` during `--channel beta` installs (both `install.sh` and `install.ps1`), so the daemon's self-update mechanism no longer silently defaults to stable. The init wizard preserves an existing beta channel from config. ([#1377](https://github.com/netclaw-dev/netclaw/pull/1377))

---

#### 0.24.0-beta.2 2026-06-09 ####

Netclaw v0.24.0-beta.2 — Channel delivery descriptor registry, TUI improvements, and dependency updates

**Features**

* **Channel delivery descriptor registry** — new registration-based system for channel delivery descriptors, improving extensibility of channel integrations. ([#1326](https://github.com/netclaw-dev/netclaw/pull/1326))

* **Native text selection in TUI** — text selection in the terminal UI is now handled natively via Termina 0.11.0, enabling proper copy/paste behavior. ([#1359](https://github.com/netclaw-dev/netclaw/pull/1359))

**Bug Fixes**

* **TUI list views are now scrollable** — fixed unresponsive scrolling in all TUI list views. ([#1363](https://github.com/netclaw-dev/netclaw/pull/1363))

* **DaemonApi threaded into init wizard's provider step** — fixed the init wizard's provider step to properly use the DaemonApi. ([#1369](https://github.com/netclaw-dev/netclaw/pull/1369))

**Dependencies**

* **Verify.XunitV3 bumped to 31.19.1** — test framework update. ([#1367](https://github.com/netclaw-dev/netclaw/pull/1367))

* **Aspire.Hosting.Testing bumped to 13.4.3** — .NET Aspire test hosting update. ([#1366](https://github.com/netclaw-dev/netclaw/pull/1366))

---

#### 0.24.0-beta.1 2026-06-08 ####

Netclaw v0.24.0-beta.1 — Shell streaming, media improvements, bug fixes, and dependency updates

**Features**

* **Shell streaming support** — `shell_execute` output now streams incrementally instead of waiting for full completion, fixing the hard 90s timeout for long-running commands. ([#1360](https://github.com/netclaw-dev/netclaw/pull/1360))

**Bug Fixes**

* **Bound per-turn empty/thinking-only response loops** — prevents agents from getting stuck in infinite loops of empty or thinking-only responses within a single turn. ([#1358](https://github.com/netclaw-dev/netclaw/pull/1358))

* **MCP static Authorization header no longer triggers OAuth discovery** — when a static `Authorization` header is configured for MCP servers, Netclaw skips the OAuth discovery step, fixing conflicts with header-based auth. ([#1357](https://github.com/netclaw-dev/netclaw/pull/1357))

* **Media resize at ingest only** — images are now resized once during ingestion instead of being re-inlined on every turn, improving performance and reducing redundant processing. ([#1296](https://github.com/netclaw-dev/netclaw/pull/1345))

* **Secret placeholder writeback prevented** — file read/write tools no longer write back secret placeholders, fixing a data integrity issue. ([#1343](https://github.com/netclaw-dev/netclaw/pull/1343))

* **TUI approval detail toggle remapped** — the approval detail toggle key is now `Ctrl+O` (was `Ctrl+V`), freeing up `Ctrl+V` for its expected use. ([#1362](https://github.com/netclaw-dev/netclaw/pull/1362))

* **Model manager manual entry state reset** — fixed a bug where the model manager retained stale manual entry state, causing confusion during model selection. ([#1344](https://github.com/netclaw-dev/netclaw/pull/1344))

* **Docker root-drop log cleanup** — removed noisy log output from the CLI launcher when running as root in Docker. ([#1342](https://github.com/netclaw-dev/netclaw/pull/1342))

**Dependencies**

* **Anthropic bumped to 12.27.0** — pulls in latest Anthropic SDK improvements. ([#1352](https://github.com/netclaw-dev/netclaw/pull/1352))

* **Discord.Net bumped to 3.20.1** — updates Discord integration library. ([#1353](https://github.com/netclaw-dev/netclaw/pull/1353))

* **Termina bumped to 0.11.0** — updates the terminal/session management library. ([#1354](https://github.com/netclaw-dev/netclaw/pull/1354))

---

#### 0.23.0 2026-06-06 ####

Netclaw v0.23.0 — Beta channel, streaming-native chat, Docker improvements, and shell reliability fixes

**Features**

* **Opt-in beta release channel** — Netclaw can now publish and install prereleases without ever touching a default install. The release manifest gained an additive `latestPrerelease` pointer (newest of all releases) alongside `latest` (newest stable); `--channel beta` / `-Channel beta` and the rolling `:beta` Docker tag resolve to it, while stable clients structurally only ever read `latest`. ([#1314](https://github.com/netclaw-dev/netclaw/pull/1314))

* **Channel-aware, SemVer-correct update check** — the binary update check now honors `Daemon.UpdateChannel` (`stable` default, or `beta`) and compares versions by SemVer 2.0.0 precedence. Beta testers are notified of the next prerelease and automatically roll onto a stable release once it supersedes their beta; stable users are never offered a prerelease. ([#1315](https://github.com/netclaw-dev/netclaw/pull/1315))

* **Streaming-native chat-client stack with composable routing** — the `IChatClient` stack has been redesigned so the streaming-vs-non-streaming transport distinction no longer leaks into callers. Netclaw now issues only streaming requests across all 8 providers, eliminating the OpenAI Codex `400 "Stream must be set to true"` error and preventing reasoning content drops on some providers. Resilience and observability are compositional via `Microsoft.Extensions.AI.ChatClientBuilder` — each (provider, model) pipeline is assembled as `Logging → Retry → VendorOptions → raw`. A `RoutingChatClient` walks ordered candidate lists for failover, and `LoggingChatClient` is now stateless with session-correlated log tags (`SessionId`). The transport-level retry policy is configurable via `Session:Tuning:StreamingRetryPolicy`. ([#1313](https://github.com/netclaw-dev/netclaw/pull/1313))

* **Bounded tool output with file spill** — large tool outputs are now bounded and spilled to a file instead of flooding the session, keeping context lean while preserving the full output on disk. ([#1305](https://github.com/netclaw-dev/netclaw/pull/1305))

**Bug Fixes**

* **Shell approval patterns no longer match bare integers** — fixed a bug where numeric argument patterns (e.g., `1`, `42`) were incorrectly matched as approval patterns for shell commands. ([#1331](https://github.com/netclaw-dev/netclaw/pull/1331))

* **Shell pipe reads are bounded** — pipe reads are now bounded to `MaxOutputChars` before truncating, preventing runaway memory on huge command output. ([#1298](https://github.com/netclaw-dev/netclaw/pull/1298))

* **Shell verifies the working directory** — Netclaw confirms the working directory exists before launching a process, surfacing a clear error instead of a cryptic failure. ([#1299](https://github.com/netclaw-dev/netclaw/pull/1299))

* **Docker self-dropping CLI launcher for root exec** — `/usr/local/bin/netclaw` now transparently re-execs as the `netclaw` user when invoked as root, so `docker exec`/`kubectl exec -- netclaw` works on any orchestrator without `gosu`/`-u` knowledge required. ([#1322](https://github.com/netclaw-dev/netclaw/pull/1322))

* **Docker container no longer crash-loops on read-only `/tools` mount** — the entrypoint now treats `/tools` as best-effort and never recursively chowns it. ([#1321](https://github.com/netclaw-dev/netclaw/pull/1321))

* **Non-root agent can install tools at runtime in Docker** — the image now ships user-writable, on-`PATH` install locations (`~/.local/bin`, `~/.dotnet`, `~/.dotnet/tools`, and a mounted `/tools/bin`) so runtime-installed tools resolve as bare commands. ([#1321](https://github.com/netclaw-dev/netclaw/pull/1321))

* **Docker reaps orphaned subprocesses** — the container now uses `tini` so orphaned subprocesses are reaped instead of accumulating as zombies. ([#1306](https://github.com/netclaw-dev/netclaw/pull/1306))

* **Docker owns the daemon lifecycle** — the container is now the sole owner of the daemon lifecycle, fixing conflicting restart behavior. ([#1282](https://github.com/netclaw-dev/netclaw/pull/1282))

* **Docker bind-mount ownership repaired** — bind-mount ownership is corrected on startup so mounted data is writable. ([#1281](https://github.com/netclaw-dev/netclaw/pull/1281))

* **Provider modality probing fixed** — Netclaw no longer persists guessed modalities, and the model-probe timeout and visibility issues are resolved. ([#1311](https://github.com/netclaw-dev/netclaw/pull/1311))

* **OpenAI Codex calls no longer hang** — non-streaming Codex calls are now served via streaming under the hood, so they complete reliably. ([#1289](https://github.com/netclaw-dev/netclaw/pull/1289))

* **Zero context-window models ignored** — models that report a zero context window are now ignored instead of breaking model selection. ([#1285](https://github.com/netclaw-dev/netclaw/pull/1285))

* **Init wizard readiness race fixed** — init readiness is now gated on a daemon restart generation and a re-resolved endpoint, closing a race in the setup wizard. ([#1307](https://github.com/netclaw-dev/netclaw/pull/1307))

* **Standardized self-animating spinner across probe surfaces** — replaced five hand-rolled, drifted probe/validation spinners with Termina's shared `SpinnerNode` helper. Fixes the frozen ModelManager spinner, the ~1fps crawl on provider validation/probe spinners, and inconsistent animation cadence. ([#1312](https://github.com/netclaw-dev/netclaw/pull/1312), [#1327](https://github.com/netclaw-dev/netclaw/pull/1327))

* **Lighter daemon memory footprint** — `netclawd` now uses Workstation GC, reducing baseline memory use. ([#1295](https://github.com/netclaw-dev/netclaw/pull/1295))

* **Windows installer uses User-scope PATH** — the Windows install instruction now updates the User-scope PATH so `netclaw` is found in new shells. ([#1274](https://github.com/netclaw-dev/netclaw/pull/1274))

* **Corrected version split in Directory.Build.props** — the version parsing logic that extracts major.minor.patch/prerelease segments was fixed to handle all version strings correctly. ([#1339](https://github.com/netclaw-dev/netclaw/pull/1339))

**Dependency Updates**

* **ModelContextProtocol.Core and ModelContextProtocol.AspNetCore bumped to 1.4.0** — pulls in upstream MCP SDK improvements, including better streaming support that enables the new chat-client stack. ([#1329](https://github.com/netclaw-dev/netclaw/pull/1329), [#1330](https://github.com/netclaw-dev/netclaw/pull/1330))

* **Aspire.Hosting.AppHost bumped to 13.4.2** — .NET Aspire host updates. ([#1318](https://github.com/netclaw-dev/netclaw/pull/1318))

* **CommunityToolkit.Aspire.Hosting.Ollama bumped to 13.4.0** — Ollama hosting support update. ([#1320](https://github.com/netclaw-dev/netclaw/pull/1320))

* **Grpc.Tools bumped to 2.81.0** — gRPC tooling update. ([#1269](https://github.com/netclaw-dev/netclaw/pull/1269))

* **Verify.XunitV3 bumped to 31.19.0** — test framework update. ([#1270](https://github.com/netclaw-dev/netclaw/pull/1270))

**Documentation & Internal**

* **Documented beta/stable release process** — added documentation for the release workflow. ([#1323](https://github.com/netclaw-dev/netclaw/pull/1323))

* **Cited #648 at the chat-client routing seam** — added documentation reference for provider routing behavior. ([#1335](https://github.com/netclaw-dev/netclaw/pull/1335))

* **Archived 8 completed OpenSpec changes** — cleaned up completed specification work and synced delta specs. ([#1325](https://github.com/netclaw-dev/netclaw/pull/1325))

* **Added `.claude/worktrees` to `.gitignore`** — keeps Claude Code worktree artifacts out of version control. ([#1336](https://github.com/netclaw-dev/netclaw/pull/1336))

* **Prepared v0.23.0-beta.5 release** — release preparation commits for beta channel. ([#1337](https://github.com/netclaw-dev/netclaw/pull/1337))

* **Prepared v0.23.0-beta.4 release** — release preparation commits for beta channel. ([#1328](https://github.com/netclaw-dev/netclaw/pull/1328))

* **Prepared v0.23.0-beta.1 release** — release preparation commits for beta channel. ([#1317](https://github.com/netclaw-dev/netclaw/pull/1317))
