## Context

NetClaw currently has no Aspire infrastructure, no `samples/` folder, and no curated demo path. Every evaluator must stand up Slack credentials, provider API keys, and channel routing by hand before exercising the agent. PR #1095 (Mattermost channel) is the missing piece that lets a fully ephemeral self-hosted stack be assembled — but realizing it requires bridging several things that don't exist yet in the codebase: an AppHost, a post-startup secret-seeding pattern (Mattermost admin/team/bot must be created at runtime before the daemon can authenticate), and integration glue that respects NetClaw's existing security model (`ExposureMode`, `LoopbackAuthenticationHandler`, `NetclawPaths`, `Daemon.Lock` file, etc.).

Two facts shape every decision below:

1. **The existing `NETCLAW_HOME` env var already solves the state-isolation problem.** Audit during planning revealed the mechanism is implemented in `NetclawPaths.cs:113`, fully tested in `NetclawPathsTests.cs`, used daily by the smoke + eval rigs, and frozen as a `netclaw-cli` spec requirement. No daemon code change is needed for the demo to sandbox SQLite, secrets, keys, and identity files away from any host-installed NetClaw.
2. **Containerizing the demo daemon collides with NetClaw's security model.** `ExposureModeValidationService` refuses non-loopback `Host` binds under `ExposureMode.Local`, and `LoopbackAuthenticationHandler` won't grant operator identity to source IPs outside `IPAddress.IsLoopback`. Workarounds exist (Docker `--network host`, a new `ExposureMode`) but each is a separate design effort that this demo deliberately does not undertake.

## Goals / Non-Goals

**Goals:**

- Single command (`dotnet run --project samples/Netclaw.Demo.AppHost`) brings up a working bot the operator can chat with in Mattermost.
- Zero credential entry from the operator: admin user, team, bot user, bot token, and default channel are all seeded by the AppHost at startup.
- Zero state contamination of any host-installed NetClaw: SQLite, keys, secrets, identity files, workspaces, logs all live under `<demoHome>/.netclaw/`.
- Existing NetClaw security guarantees preserved: `ExposureMode.Local`, loopback auth, fail-closed startup, default-deny ACL, `Slopwatch` clean.
- Agent-drivable verification — Aspire MCP exposes the running app so an LLM agent can list resources, fetch logs, hit health, and post messages without human intervention.

**Non-Goals:**

- Production-grade Mattermost (TE image + Postgres). Preview image is ephemeral and intentional.
- Mattermost interactive button callbacks. `CallbackUrl` left unset; approvals fall back to text-reply mode.
- SearXNG / Playwright MCP integration. Out of v1 scope.
- Wiring `Netclaw.ServiceDefaults` into the production `Netclaw.Daemon`. The project is created (per skill conventions) but `AddServiceDefaults()` is not called from daemon `Program.cs`.
- A new `ExposureMode` for orchestrated/containerized deployments.
- CI execution of the integration test on every push. Test is `[Trait("Category", "SlowSmoke")]` and runs on demand.
- Multi-host or remote-evaluator scenarios. Local-only.

## Decisions

### Decision 1: Daemon as host process (Aspire `AddProject<>`), not containerized

**Choice:** Run the demo daemon as a host process via `builder.AddProject<Projects.Netclaw_Daemon>("daemon")`. Other resources (Mattermost, Ollama, bootstrap) are containers managed by Aspire.

**Alternatives considered:**

- *Containerize the daemon with the existing `docker/Dockerfile`.* Rejected. `ExposureModeValidationService.cs:85-98` aborts startup when `ExposureMode=Local` and `Host != 127.0.0.1/::1`, and `LoopbackAuthenticationHandler.cs:40-67` grants operator identity only to loopback source IPs. Inside a Docker bridge network, neither the daemon's bind nor the source IPs of caller requests satisfy these checks. The eval rig sidesteps this with `docker run --network host`, but Aspire's container model doesn't cleanly express host networking, and it's Linux-only (degraded on Mac/Windows Docker Desktop).
- *Add a new `ExposureMode.Orchestrated` paired with a shared-secret auth scheme.* Real design work, real security review needed, would need a frozen spec requirement of its own. Out of scope.

**Rationale:** Host process preserves NetClaw's existing security posture without a single change to the daemon, gives the fastest edit/build/run loop during demo development, and matches how `NETCLAW_HOME`-isolated daemons are already exercised by the smoke harness.

### Decision 2: Reuse existing `NETCLAW_HOME` for state isolation

**Choice:** AppHost sets `NETCLAW_HOME=<repoRoot>/samples/Netclaw.Demo.AppHost/.demo-home/.netclaw` on the daemon resource. `NetclawPaths.cs:113` already honors this with tilde expansion and proper precedence.

**Alternatives considered:**

- *Add a new `NETCLAW_BASE_PATH` env var.* Pursued during planning until the audit found `NETCLAW_HOME` already exists. Net new env var would have been redundant; PR was cancelled before any code was written.
- *Override `HOME` / `USERPROFILE` instead.* Rejected. Eight other production callsites (`PathExpansion`, `ExternalSkillsConfig`, `ShellCommandPolicy`, `DaemonManager`, `UpdateCommand`, `BrowserAutomationRuntimeDetector`, `IdentityStepViewModel`, `CrashLogWriter`) intentionally read the *real* operator home (for real Chrome installs, real `~/.claude/skills`, real CLI install). Redirecting `HOME` would mis-route those.

**Rationale:** `NETCLAW_HOME` is exactly the right granularity — it redirects NetClaw's own state and nothing else. It's already a frozen spec requirement, so this design is on solid ground.

### Decision 3: Daemon binds non-default port `5299`

**Choice:** AppHost sets `NETCLAW_Daemon__Port=5299` (mirroring the eval rig's `NETCLAW_EVAL_PORT`), explicit `NETCLAW_Daemon__Host=127.0.0.1`, and explicit `NETCLAW_Daemon__ExposureMode=local`.

**Rationale:** Avoids colliding with any host-running daemon on the default `5199` — both at the port level and at the daemon lock file (`DaemonManager.cs:249,315`). Explicit `Host` and `ExposureMode` document intent even when they equal defaults; future readers don't have to know the defaults to reason about security posture.

### Decision 4: Ollama via `CommunityToolkit.Aspire.Hosting.Ollama`, default model `qwen3.5:2b-q4_K_M`

**Choice:** `builder.AddOllama("ollama").AddModel("ollama-model", "qwen3.5:2b-q4_K_M").WithDataVolume();`

**Alternatives considered:**

- *Custom container resource with a sidecar init step running `ollama pull`.* Rejected for v1 — more code, no advantage over the community package which already supports model pull + cache volumes.
- *Manual `ollama pull` prerequisite documented in the README.* Rejected. Defeats the "zero friction" goal.
- *Different default model.* `qwen3:30b` and `qwen3:14b` (NetClaw's primary/fallback for production) are too large for CPU. `qwen3.5:0.8b` is lighter but has weak public evidence for reliable tool calling; `qwen3.5:4b` and `qwen3.5:9b` are materially heavier on Ollama and erode the CPU-latency goal.

**Rationale:** Community package is purpose-built for this use case. `qwen3.5:2b-q4_K_M` is the least-risk compromise between local CPU latency and tool-calling usefulness we found after comparing the Ollama tag sizes and Qwen's function-calling guidance: smaller than the previous `qwen3:4b`, still within the Qwen 3.5 family, and materially lighter than the 4B/9B Ollama variants. If Phase 5 smoke testing still shows unacceptable tool behavior, the next fallback is not "bigger model first" — it's a tighter fast-profile tool surface.

### Decision 5: Mattermost preview image, not Team Edition + Postgres

**Choice:** `mattermost/mattermost-preview` with the env vars used by `MattermostFixture` (`MM_SERVICESETTINGS_ENABLEBOTACCOUNTCREATION=true`, `ENABLEUSERACCESSTOKENS=true`, `ENABLEOPENSERVER=true`, `ENABLETESTING=true`).

**Rationale:** Self-contained (no separate Postgres), zero licensing friction, no admin setup wizard. Preview image is deprecated upstream — documented in the README as a future migration. Acceptable trade for a demo.

### Decision 6: Mattermost bootstrap via in-AppHost readiness hook

**Choice:** Subscribe to Mattermost `ResourceReadyEvent` directly inside the AppHost, run `MattermostBootstrapper.SeedAsync(...)` once, cache the `BootstrapResult` in a `TaskCompletionSource`, and have the daemon's async `WithEnvironment(...)` callback await that result before process launch.

**Alternatives considered:**

- *Separate executable project resource.* Rejected — cross-process output plumbing bought us complexity without meaningful value because the seed sequence is already encapsulated in `MattermostBootstrapper` and only needs to run once per AppHost session.
- *Inline seeding without a dedicated ready-event gate.* Rejected — loses explicit ordering against Mattermost readiness and makes retries/idempotency harder to reason about.

**Rationale:** The shared `MattermostBootstrapper` library already gives us the reuse and testability we wanted. Keeping the bootstrap in-process avoids extra resource churn, avoids JSON/stdout marshaling, and still preserves correct startup ordering because the daemon's env-var callback cannot complete until seeding succeeds.

### Decision 7: Extract `MattermostBootstrapper` from `MattermostFixture`

**Choice:** New library `src/Netclaw.Channels.Mattermost.Bootstrap` exposing `MattermostBootstrapper.SeedAsync(Uri serverUrl, BootstrapOptions opts, CancellationToken)` returning `BootstrapResult { ServerUrl, BotUserId, BotToken, TeamId, DefaultChannelId }`. `MattermostFixture.cs:196-260` refactors to delegate.

**Rationale:** The integration test's seeding sequence is exactly what the demo needs. Keeping it forked between two callers invites drift. Extracting it now also de-risks future MCP/SearXNG/Playwright bootstrap helpers — establishes the shape.

### Decision 8: Skip `CallbackUrl`, fall back to text-reply approvals

**Choice:** `NETCLAW_Mattermost__CallbackUrl` deliberately unset. Mattermost interactive buttons aren't used; approvals come through as plain replies.

**Alternatives considered:**

- *Expose the daemon to the Mattermost container.* Requires the daemon to bind beyond loopback (collides with `ExposureMode.Local`) or sets up a host-side reverse proxy. Out of scope.
- *Run a proxy container that forwards Mattermost callbacks to the host daemon's loopback.* Possible but adds complexity for a demo niche.

**Rationale:** Text-reply mode is fully supported (`MattermostChannelOptions.CallbackUrl` is optional). Demo doesn't need approvals to be button-driven to be useful.

### Decision 9: `Netclaw.ServiceDefaults` created but not wired

**Choice:** Ship the project for canonical Aspire conventions and as a vehicle for future production observability work, but do not call `AddServiceDefaults()` from `Netclaw.Daemon/Program.cs` in this PR.

**Rationale:** Skill guidance is unambiguous — every Aspire service should reference a `ServiceDefaults` project. Creating it now is a no-cost forward-compatibility move. Wiring it into the daemon touches production observability (OTel exporters, health-check endpoint shape, resilience handlers) and deserves a dedicated PR with its own perf/regression validation.

### Decision 10: Aspire MCP integration for agent-driven verification

**Choice:** Configure Aspire's built-in MCP server (precise API surface verified during implementation) and register it at the repo root via `.mcp.json` so a Claude Code session can connect, list resources, fetch logs, hit health endpoints, and POST messages to Mattermost via the seeded test user.

**Rationale:** Each subsequent phase's "verify" step turns from "ask a human to click around" into "the agent self-verifies." Particularly valuable for Phase 5 (integration test) — the implementing agent can iterate on the smoke test by driving the AppHost directly. Also documents the demo as an MCP exemplar for downstream users who want to do the same.

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| `qwen3.5:2b-q4_K_M` still mis-routes tool calls in some prompts (small-model reliability). | README documents the fast-profile steering, disables thinking mode, and keeps tool-loop caps low. If Phase 5 smoke reveals frequent failures, tighten the fast-profile tool surface before moving to a larger default model. |
| Aspire 9.x packages may not target `.NET 10`. | Phase 1 verifies compatibility. If only `net9.0` is supported, AppHost project sets `<TargetFramework>net9.0</TargetFramework>` while the rest of the repo stays `net10.0`; `global.json` `rollForward: major` already covers SDK pinning. |
| `mattermost-preview` is deprecated upstream. | Pinned in the README as a future migration. Functionally stable; the Mattermost org hasn't removed the image. |
| Bootstrap-resource design (project vs lifecycle hook) is novel in Aspire. | Phase 2 prototypes the hook approach first as a complexity check; escalates to executable project resource if ordering or testability becomes painful. |
| Cold first run pulls ~3.6GB of images + models. | Documented expected timing in README. Cached on subsequent runs via Docker volumes and `WithDataVolume()`. |
| Public-audience fast profile still exposes enough tools for a small model to pick bad branches. | Keep the public prompt lean, cap tool loops, disable thinking mode, and prefer a lighter default model. If failures persist, tighten the fast-profile tool surface before increasing model size. |
| Port `5299` already in use locally. | Daemon fails fast at startup; AppHost dashboard surfaces the bind error. README troubleshooting section calls this out. |
| `.demo-home/` accidentally committed by an operator. | Added to `.gitignore` in Phase 1. |

## Migration Plan

Pure additive; no migration. Rollback is `git revert` of the merge commit.

Phasing within the change:

1. **Phase 1 — Foundations.** ServiceDefaults + AppHost skeleton + daemon resource with `NETCLAW_HOME`/port/Host/ExposureMode env vars + `.gitignore` + solution wiring. Verify `dotnet run` launches the dashboard with daemon healthy.
2. **Phase 1.5 — Aspire MCP.** Register MCP server, confirm Claude Code can drive the running app.
3. **Phase 2 — Mattermost orchestration.** Extract `MattermostBootstrapper`, add Mattermost container, run the bootstrap sequence from the AppHost ready hook, wire daemon env vars from the captured bootstrap result.
4. **Phase 3 — Ollama.** Add Ollama container + model pull, wire provider config.
5. **Phase 4 — Demo defaults + README.** Finalize env-driven demo defaults, write README, update operations skill.
6. **Phase 5 — Aspire integration test.** xUnit + `Aspire.Hosting.Testing` smoke gate.
7. **Phase 6 — Repo-wide finishing.** Slopwatch, file-header verify, docs, optional CI workflow_dispatch.

## Open Questions

1. **Fast-profile tool surface vs latency.** How much of the default public audience should remain exposed for the demo before tool-calling drift outweighs the value of showing tools at all? Resolved incrementally during implementation by shrinking prompt weight, disabling thinking mode, and moving to a smaller default model.
2. **Aspire dashboard MCP API surface.** The skill descriptions don't pin down whether it's `aspire mcp` CLI vs `builder.AddMcpServer()` API. Resolved by inspection in Phase 1.5; documented in the demo README.
3. **`Aspire.Hosting.Testing` xUnit support on `net10.0`.** May require an attribute or `[ModuleInitializer]` adjustment per `dotnet-skills:aspire-integration-testing`. Resolved in Phase 5.
4. **Whether the bootstrap sequence should run on every AppHost start, or detect "already seeded" via a sentinel file in `.demo-home/`.** Idempotency would be friendlier across restarts. Resolved in Phase 2 design.

## Actor / Persistence / Failure Notes

- **Actor boundaries unchanged.** The demo daemon runs the same Akka.NET actor system as production; no new actors, no new messages. The bootstrap sequence stays outside the actor system — AppHost orchestration calls Mattermost REST and injects env vars before the daemon starts.
- **Persistence isolated.** Daemon SQLite at `<demoHome>/.netclaw/netclaw.db`; Mattermost preview uses its own ephemeral SQLite-via-image; Ollama caches in a named Docker volume. No shared persistence with any host-installed NetClaw.
- **Failure modes:**
  - Mattermost container fails to start → Aspire reports unhealthy; bootstrap and daemon `WaitFor` block; AppHost surfaces in dashboard.
  - Bootstrap fails (Mattermost not ready, signup disabled, network) → bootstrap exits non-zero; daemon never starts; clear error in dashboard.
  - Ollama model pull fails → `CommunityToolkit.Aspire.Hosting.Ollama` retries with backoff; daemon `WaitFor` blocks.
  - Daemon port collision → daemon fails fast on `IPListener` bind; lock-file conflict surfaces as `DaemonManager` exception.
  - `qwen3.5:2b-q4_K_M` refuses or mis-routes a tool call → bot replies in plain text or burns part of its loop budget; demo continues functioning (degraded but useful).
- **Recovery:** Demo is "stateless from the operator's perspective" — `Ctrl+C` the AppHost, optionally `docker volume rm` for full reset, then `dotnet run` again.
