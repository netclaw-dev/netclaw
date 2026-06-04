#### 0.23.0-beta.2 2026-06-03 ####

Netclaw v0.23.0-beta.2 — beta channel fix release for non-root container deployments

This is a prerelease on the beta channel. Stable installs are unaffected and remain on 0.22.1; only opt-in beta testers are offered this build.

**Try the beta**

* Linux / macOS: `curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- --channel beta`
* Windows: download `install.ps1`, then `./install.ps1 -Channel beta`
* Docker: `docker pull ghcr.io/netclaw-dev/netclaw:beta`

**Bug Fixes**

* **Container no longer crash-loops on a read-only `/tools` mount** — the bind-mount ownership repair added in 0.23.0-beta.1 (#1281) ran a recursive `chown` over `/tools` and aborted fatally when the mount was read-only, crash-looping the container. `/tools` is a PATH directory the agent only reads from, so the entrypoint now treats it as best-effort: it never recursively chowns it and never fails when it is read-only or already correctly owned. ([#1321](https://github.com/netclaw-dev/netclaw/pull/1321))

* **Non-root agent can install tools at runtime again** — because the daemon runs as the unprivileged `netclaw` user it cannot write system `PATH` directories or `apt-get` without root. The image now ships user-writable, on-`PATH` install locations (`~/.local/bin`, `~/.dotnet`, `~/.dotnet/tools`, and a mounted `/tools/bin`) so a runtime-installed `dotnet`, `pip --user` tool, or .NET global tool resolves as a bare command in the agent's non-interactive shell. ([#1321](https://github.com/netclaw-dev/netclaw/pull/1321))

#### 0.23.0-beta.1 2026-06-03 ####

Netclaw v0.23.0-beta.1 — our first public beta, debuting the opt-in beta release channel itself

This is a prerelease. Stable installs are unaffected — the default install, Docker `:latest`, and the GitHub "Latest" release all still point at 0.22.1, and only opt-in beta testers are ever offered this build.

**Try the beta**

* Linux / macOS: `curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- --channel beta`
* Windows: download `install.ps1`, then `./install.ps1 -Channel beta`
* Docker: `docker pull ghcr.io/netclaw-dev/netclaw:beta`
* To get update notices on the beta line, set `Daemon.UpdateChannel` to `beta` in your config.

**Features**

* **Opt-in beta release channel** — Netclaw can now publish and install prereleases without ever touching a default install. The release manifest gained an additive `latestPrerelease` pointer (newest of all releases) alongside `latest` (newest stable); `--channel beta` / `-Channel beta` and the rolling `:beta` Docker tag resolve to it, while stable clients structurally only ever read `latest`. ([#1314](https://github.com/netclaw-dev/netclaw/pull/1314))

* **Channel-aware, SemVer-correct update check** — the binary update check now honors `Daemon.UpdateChannel` (`stable` default, or `beta`) and compares versions by SemVer 2.0.0 precedence. Beta testers are notified of the next prerelease and automatically roll onto a stable release once it supersedes their beta; stable users are never offered a prerelease. ([#1315](https://github.com/netclaw-dev/netclaw/pull/1315))

* **Bounded tool output with file spill** — large tool outputs are now bounded and spilled to a file instead of flooding the session, keeping context lean while preserving the full output on disk. ([#1305](https://github.com/netclaw-dev/netclaw/pull/1305))

**Bug Fixes**

* **Provider modality probing fixed** — Netclaw no longer persists guessed modalities, and the model-probe timeout and visibility issues are resolved. ([#1311](https://github.com/netclaw-dev/netclaw/pull/1311))

* **OpenAI Codex calls no longer hang** — non-streaming Codex calls are now served via streaming under the hood, so they complete reliably. ([#1289](https://github.com/netclaw-dev/netclaw/pull/1289))

* **Docker reaps orphaned subprocesses** — the container now uses `tini` so orphaned subprocesses are reaped instead of accumulating as zombies; smoke setup was deduplicated. ([#1306](https://github.com/netclaw-dev/netclaw/pull/1306))

* **Init wizard readiness race fixed** — init readiness is now gated on a daemon restart generation and a re-resolved endpoint, closing a race in the setup wizard. ([#1307](https://github.com/netclaw-dev/netclaw/pull/1307))

* **Docker owns the daemon lifecycle** — the container is now the sole owner of the daemon lifecycle, fixing conflicting restart behavior. ([#1282](https://github.com/netclaw-dev/netclaw/pull/1282))

* **Shell pipe reads are bounded** — pipe reads are now bounded to `MaxOutputChars` before truncating, preventing runaway memory on huge command output. ([#1298](https://github.com/netclaw-dev/netclaw/pull/1298))

* **Shell verifies the working directory** — Netclaw confirms the working directory exists before launching a process, surfacing a clear error instead of a cryptic failure. ([#1299](https://github.com/netclaw-dev/netclaw/pull/1299))

* **Lighter daemon memory footprint** — `netclawd` now uses Workstation GC, reducing baseline memory use. ([#1295](https://github.com/netclaw-dev/netclaw/pull/1295))

* **Zero context-window models ignored** — models that report a zero context window are now ignored instead of breaking model selection. ([#1285](https://github.com/netclaw-dev/netclaw/pull/1285))

* **Docker bind-mount ownership repaired** — bind-mount ownership is corrected on startup so mounted data is writable. ([#1281](https://github.com/netclaw-dev/netclaw/pull/1281))

* **Windows installer uses User-scope PATH** — the Windows install instruction now updates the User-scope PATH so `netclaw` is found in new shells. ([#1274](https://github.com/netclaw-dev/netclaw/pull/1274))

#### 0.22.1 2026-06-01 ####

Netclaw v0.22.1 — Quick bug-fix release on the heels of 0.22.0

**Bug Fixes**

* **GitHub Copilot provider additions now persist** — the TUI provider manager was losing newly added Copilot providers between sessions. Additions now stick correctly. ([#1271](https://github.com/netclaw-dev/netclaw/pull/1271))

* **OpenAI Codex models discovered live** — Netclaw can now probe the OpenAI API to discover Codex models at runtime, auto-populating context windows and modalities. No more waiting for a software update to access the latest Codex features. ([#1268](https://github.com/netclaw-dev/netclaw/pull/1268))

* **ChatGPT OAuth account ID resolution restored** — the OAuth flow for ChatGPT providers was returning the wrong account identifier, breaking authentication. Fixed. ([#1263](https://github.com/netclaw-dev/netclaw/pull/1263))

* **Vision model images no longer dropped between turns** — when using vision-capable models, images attached via tool calls were being lost when crossing turn boundaries. Images now survive turn transitions properly. ([#1265](https://github.com/netclaw-dev/netclaw/pull/1265))

* **CLI no longer crashes when daemon is offline** — attempting to chat while the Netclaw daemon was unreachable would crash the CLI. It now handles this gracefully and provides useful feedback. ([#1258](https://github.com/netclaw-dev/netclaw/pull/1258))

#### 0.22.0 2026-05-31 ####

Netclaw v0.22.0 — Subagents that actually stick around, sessions that remember their place, and a few long-standing gremlines squashed

**Features**

* **Files that aren't text? Now handled properly** — when Netclaw reads a file that could contain an image, audio, or other non-text content, it now classifies it upfront instead of silently guessing or skipping over it. Along with a brand-new unified file-type classifier that replaced the old patchwork of per-source detection logic, this means attachments, uploads, and multimodal reads all work from the same reliable taxonomy. No more "huh, where did that go?" ([#1245](https://github.com/netclaw-dev/netclaw/pull/1245), [#1240](https://github.com/netclaw-dev/netclaw/pull/1240))

**Bug Fixes**

* **Subagents that don't vanish mid-task** — this release packs three subagent hardening fixes into one release. The inactivity watchdog now pauses while waiting for your approval instead of ticking down and killing a perfectly healthy subagent ([#1203](https://github.com/netclaw-dev/netclaw/pull/1203)). Headless execution is more resilient to edge-case failures ([#1236](https://github.com/netclaw-dev/netclaw/pull/1236)). And subagents now share the same two-phase streaming watchdog as the main session, so they don't silently lose progress tracking anymore ([#1243](https://github.com/netclaw-dev/netclaw/pull/1243)). If you've ever watched a subagent stall out for no reason, this should feel like a breath of fresh air.

* **Tool approvals that survive a restart** — previously, if Netclaw restarted between you approving a tool call and it actually running, the session could lose track of what you approved and start confusing you with re-prompts. Approval context now persists through cold restarts and passivation — you say yes once, it stays yes. ([#1219](https://github.com/netclaw-dev/netclaw/pull/1219))

* **That annoying tool-loop? Gone** — if Netclaw's volatile per-turn context (time, working notes, memory recall) got placed after your message, some LLMs would mistakenly treat it as a signal to keep going, spiraling into tool loops. We moved it before the user message where it belongs (as a System-role prefix), and the spin is gone. ([#1218](https://github.com/netclaw-dev/netclaw/pull/1218))

* **Session logs that actually stay complete** — on Windows especially, AV scanners would occasionally grab the log file mid-write and cause lines to vanish without a trace. We switched to a persistent file handle instead of opening/closing per line, and the flake is fully resolved. Your session logs now stay intact. ([#1254](https://github.com/netclaw-dev/netclaw/pull/1254), [#1246](https://github.com/netclaw-dev/netclaw/pull/1246))

* **Webhooks and autonomous tasks can finally run shells** — the tools subsystem had a blocking path that quietly prevented non-interactive shell execution in webhook and autonomous contexts. If you've ever tried to trigger a shell command from a webhook and gotten nothing back, this is the fix. Webhook provenance is also now properly tracked, and the autonomous filesystem zone works as intended. ([#1250](https://github.com/netclaw-dev/netclaw/pull/1250))

* **Discord doesn't post to dead threads anymore** — after a session resume, Discord channel bindings used to carry stale state and could deliver messages to threads that no longer existed. A clean reconnect on resume fixes that. ([#1239](https://github.com/netclaw-dev/netclaw/pull/1239))

* **Slack audience overrides hit the right channel** — if you've ever set an audience override on Slack and watched it apply to the wrong channel, this is for you. Overrides are now keyed by the actual channel ID instead of a stale reference. ([#1231](https://github.com/netclaw-dev/netclaw/pull/1231))

* **Your memories stay intact** — the memory system was doing a couple sneaky things: silently dropping updates when the LLM returned nothing useful, and occasionally truncating memories on store. Neither happens now. Also, compaction-boundary summaries (the internal housekeeping notes) are filtered out of automatic recall so they don't clutter your search results. ([#1242](https://github.com/netclaw-dev/netclaw/pull/1242), [#1225](https://github.com/netclaw-dev/netclaw/pull/1225))

* **Secrets don't get corrupted** — the secrets upsert path had edge cases around concurrent edits that could corrupt or lose values. The path is now robust against those scenarios. ([#1234](https://github.com/netclaw-dev/netclaw/pull/1234))

* **Subagent approvals don't race** — the approval state machine for subagents had a few race conditions that could cause approvals to be missed or duplicated. Cleaned up. ([#1217](https://github.com/netclaw-dev/netclaw/pull/1217))

* **Self-hosted providers initialize properly** — providers pointing at self-hosted endpoints now correctly pick up their context metadata on startup. If your self-hosted endpoint was acting weird during init, this should help. ([#1227](https://github.com/netclaw-dev/netclaw/pull/1227))

**Dependencies**

* Bumped `actions/download-artifact` from 7 to 8. ([#1052](https://github.com/netclaw-dev/netclaw/pull/1052))
* Bumped `actions/setup-dotnet` from 4 to 5. ([#1166](https://github.com/netclaw-dev/netclaw/pull/1166))
* Bumped `actions/checkout` from 4 to 6. ([#1167](https://github.com/netclaw-dev/netclaw/pull/1167))
* Bumped `docker/setup-qemu-action` from 3 to 4. ([#839](https://github.com/netclaw-dev/netclaw/pull/839))
* Bumped `AButler/upload-release-assets` from 3.0 to 4.0. ([#854](https://github.com/netclaw-dev/netclaw/pull/854))
* Bumped `YamlDotNet` from 16.3.0 to 18.0.0. ([#1180](https://github.com/netclaw-dev/netclaw/pull/1180))
* Bumped `Termina` to 0.10.2. ([#1235](https://github.com/netclaw-dev/netclaw/pull/1235))
* Bumped `incrementalist.cmd` from 1.2.0 to 1.2.1. ([#1222](https://github.com/netclaw-dev/netclaw/pull/1222))
* Bumped `Anthropic` SDK from 12.23.0 to 12.24.1. ([#1221](https://github.com/netclaw-dev/netclaw/pull/1221))
* Bumped `slopwatch.cmd` from 0.4.0 to 0.4.1. ([#1223](https://github.com/netclaw-dev/netclaw/pull/1223))
* Bumped the Akka group with one update. ([#1220](https://github.com/netclaw-dev/netclaw/pull/1220))
