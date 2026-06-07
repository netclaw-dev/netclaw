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
