#### 0.22.0 2026-05-31 ####

Netclaw v0.22.0 — Subagents that actually stick around, sessions that remember their place, and a few long-standing gremlines squashed

**Features**

* **feat(media): unify file type classification (#1245)** — when Netclaw reads a file that could contain an image, audio, or other non-text content, it now classifies it upfront instead of silently guessing or skipping over it. Along with a brand-new unified file-type classifier that replaced the old patchwork of per-source detection logic, this means attachments, uploads, and multimodal reads all work from the same reliable taxonomy. No more "huh, where did that go?" ([#1245](https://github.com/netclaw-dev/netclaw/pull/1245), [#1240](https://github.com/netclaw-dev/netclaw/pull/1240))

* **feat(tools): classify multimodal file reads (#1240)** — (see above)

**Bug Fixes**

* **fix(subagents): pause inactivity watchdog while awaiting human approval (#1203)** — the inactivity watchdog was ticking down while waiting for you to approve a subagent's request, potentially killing a perfectly healthy subagent mid-task. ([#1203](https://github.com/netclaw-dev/netclaw/pull/1203))

* **fix(subagents): harden headless execution contract (#1236)** — headless subagent execution is now more resilient to edge-case failures. ([#1236](https://github.com/netclaw-dev/netclaw/pull/1236))

* **fix(subagents): two-phase streaming watchdog shared with session path (#1243)** — subagents now share the same two-phase streaming watchdog as the main session, so they don't silently lose progress tracking anymore. ([#1243](https://github.com/netclaw-dev/netclaw/pull/1243))

* **fix(subagents): harden approval lifecycle (#1217)** — the subagent approval state machine no longer has race conditions that could cause approvals to be missed or duplicated. ([#1217](https://github.com/netclaw-dev/netclaw/pull/1217))

* **fix(sessions): persist approval turn context (#1219)** — if Netclaw restarted between you approving a tool call and it actually running, the session could lose track of what you approved and start confusing you with re-prompts. Approval context now persists through cold restarts and passivation — you say yes once, it stays yes. ([#1219](https://github.com/netclaw-dev/netclaw/pull/1219))

* **fix(sessions): place volatile context before user message to stop tool-loop spin (#1216) (#1218)** — if Netclaw's volatile per-turn context (time, working notes, memory recall) got placed after the user message, some LLMs would mistakenly treat it as a signal to keep going, spiraling into tool loops. We moved it before the user message where it belongs (as a System-role prefix), and the spin is gone. ([#1218](https://github.com/netclaw-dev/netclaw/pull/1218))

* **fix(session-log): replace per-line open/close with persistent file handle (#1254)** — on Windows especially, AV scanners would occasionally grab the log file mid-write and cause lines to vanish without a trace. We switched to a persistent file handle instead of opening/closing per line, and the flake is fully resolved. Your session logs now stay intact. ([#1254](https://github.com/netclaw-dev/netclaw/pull/1254), [#1246](https://github.com/netclaw-dev/netclaw/pull/1246))

* **fix(session-log): harden append against Windows AV scan-on-close flake (#1246)** — (see above)

* **fix(tools): unblock non-interactive shell + webhook provenance + autonomous filesystem zone (#1244) (#1250)** — the tools subsystem had a blocking path that quietly prevented non-interactive shell execution in webhook and autonomous contexts. If you've ever tried to trigger a shell command from a webhook and gotten nothing back, this is the fix. Webhook provenance is also now properly tracked, and the autonomous filesystem zone works as intended. ([#1250](https://github.com/netclaw-dev/netclaw/pull/1250))

* **fix(discord): force clean reconnect after resumed session (#1239)** — after a session resume, Discord channel bindings used to carry stale state and could deliver messages to threads that no longer existed. A clean reconnect on resume fixes that. ([#1239](https://github.com/netclaw-dev/netclaw/pull/1239))

* **fix(cli): key Slack audience overrides by channel ID (#1231)** — if you've ever set an audience override on Slack and watched it apply to the wrong channel, this is for you. Overrides are now keyed by the actual channel ID instead of a stale reference. ([#1231](https://github.com/netclaw-dev/netclaw/pull/1231))

* **fix(cli): harden secrets upserts (#1234)** — the secrets upsert path had edge cases around concurrent edits that could corrupt or lose values. The path is now robust against those scenarios. ([#1234](https://github.com/netclaw-dev/netclaw/pull/1234))

* **fix(memory): harden curation tier — no silent LLM no-op + lossless update guard (#1242)** — the memory system was doing a couple sneaky things: silently dropping updates when the LLM returned nothing useful, and occasionally truncating memories on store. Neither happens now. ([#1242](https://github.com/netclaw-dev/netclaw/pull/1242))

* **fix(memory): keep compaction-boundary summaries out of automatic recall (#1225)** — compaction-boundary summaries (the internal housekeeping notes) are filtered out of automatic recall so they don't clutter your search results. ([#1225](https://github.com/netclaw-dev/netclaw/pull/1225))

* **fix(providers): parse self-hosted context metadata (#1227)** — providers pointing at self-hosted endpoints now correctly pick up their context metadata on startup. If your self-hosted endpoint was acting weird during init, this should help. ([#1227](https://github.com/netclaw-dev/netclaw/pull/1227))

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
