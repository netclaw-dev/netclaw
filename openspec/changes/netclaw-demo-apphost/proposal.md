## Why

PR #1095 landed Mattermost as a first-class channel — the first time NetClaw can be exercised end-to-end without a real Slack workspace, real provider API keys, or any external accounts. We have no companion "kick the tires" path for newcomers: every evaluator currently has to stand up Slack + Anthropic/OpenAI/OpenRouter creds + provision channels by hand before they see a single bot reply. This change ships a self-contained .NET Aspire demo (`samples/Netclaw.Demo.AppHost`) that boots NetClaw + Mattermost + Ollama with seeded credentials in a single `dotnet run`, materially lowering the activation cost for new operators (cf. PRD-001 §"Operator UX", PRD-005 §"Self-hosted provider story", PRD-009 §"Channel adapters").

## What Changes

- **New** `samples/` top-level folder and `samples/Netclaw.Demo.AppHost` Aspire AppHost project that orchestrates Mattermost (containerized, `mattermost-preview`), Ollama (containerized via `CommunityToolkit.Aspire.Hosting.Ollama` with `qwen3.5:2b-q4_K_M` model pull), and the NetClaw daemon (host process via `AddProject<>`).
- **New** `src/Netclaw.ServiceDefaults` shared Aspire project per `dotnet-skills:aspire-service-defaults` conventions (OTel, health-checks, resilience handler). Created for canonical structure; **not wired** into `Netclaw.Daemon` in this PR.
- **New** `src/Netclaw.Channels.Mattermost.Bootstrap` library extracted from the existing `MattermostFixture` integration-test setup. Exposes `MattermostBootstrapper.SeedAsync(...)` returning admin/team/bot/channel + bot token. The integration test refactors to consume this library so seeding logic lives in one place.
- **New** AppHost bootstrap sequence that runs when Mattermost reaches ready, seeds the admin/team/bot/channel/test-user state via `MattermostBootstrapper`, and feeds the resulting credentials into the daemon's async `WithEnvironment(...)` chain before the daemon process starts.
- **New** `samples/Netclaw.Demo.AppHost.IntegrationTests` xUnit project using `Aspire.Hosting.Testing` — gated behind `[Trait("Category", "SlowSmoke")]`, runs end-to-end on demand.
- **Demo daemon sandboxing** reuses the existing `NETCLAW_HOME` env var (already in `NetclawPaths.cs`, covered by tests, used by smoke + eval rigs). AppHost sets `NETCLAW_HOME=<demoHome>/.netclaw`, `NETCLAW_Daemon__Port=5299` (non-default, mirrors eval rig), `NETCLAW_Daemon__Host=127.0.0.1`, `NETCLAW_Daemon__ExposureMode=local`. No daemon code change required.
- **Aspire MCP integration** (`.mcp.json` or equivalent registration) so an agent (Claude Code or otherwise) can drive the running demo via Aspire's dashboard MCP server — letting the implementing agent self-verify each phase rather than relying on a human clicking around.
- **Docs**: new `samples/Netclaw.Demo.AppHost/README.md`, pointer from top-level `README.md`, demo section added to `feeds/skills/.system/files/netclaw-operations/SKILL.md`, smoke-test entry in `TOOLING.md`.

No breaking changes. Existing daemon, channels, providers, configuration, security, and persistence behavior are unchanged.

## Capabilities

### New Capabilities

- `demo-apphost`: orchestrated local-evaluator experience. Covers (a) Aspire AppHost responsibilities — resource composition, post-startup seeding, env-var wiring; (b) demo-time runtime defaults — sandboxed `NETCLAW_HOME`, non-default daemon port, `ExposureMode=local`, `qwen3.5:2b-q4_K_M` default model, ephemeral Mattermost; (c) self-verifiability via Aspire MCP; (d) integration-test gate; (e) operator-facing docs.

### Modified Capabilities

None. Existing capabilities (`netclaw-input-adapters` for Mattermost, `netclaw-model-providers` for Ollama, `netclaw-cli` for `NETCLAW_HOME`, `daemon-exposure` for `ExposureMode.Local`, `netclaw-testing` for the test fixture refactor) are **used as-is**. The internal extraction of `MattermostBootstrapper` from the integration-test fixture is a refactor of test-support code, not a requirement change.

## Impact

**New code:**
- `samples/Netclaw.Demo.AppHost/` (AppHost + README + appsettings)
- `samples/Netclaw.Demo.AppHost.IntegrationTests/`
- `src/Netclaw.ServiceDefaults/`
- `src/Netclaw.Channels.Mattermost.Bootstrap/`

**Modified code (internal, non-spec):**
- `Netclaw.slnx` — register new projects + `/samples/` solution folder.
- `Directory.Packages.props` — add central versions for `Aspire.Hosting.AppHost`, `Aspire.AppHost.Sdk`, `Aspire.Hosting.Testing`, `CommunityToolkit.Aspire.Hosting.Ollama`.
- `src/Netclaw.Channels.Mattermost.IntegrationTests/MattermostFixture.cs` — collapse REST seeding (lines 196–260) into a call into the new `MattermostBootstrapper`.
- `src/Netclaw.Configuration/*`, `src/Netclaw.Providers/*`, `src/Netclaw.Daemon/Configuration/*` — provider-owned `VendorOptions` plumbing used by the demo's Ollama fast path and by OpenRouter reasoning exclusion.
- `feeds/skills/.system/files/netclaw-operations/SKILL.md` — add demo section + bump `metadata.version`.
- `README.md`, `PROJECT_CONTEXT.md`, `TOOLING.md` — link/document the demo.
- `.gitignore` — add `samples/Netclaw.Demo.AppHost/.demo-home/`.

**No changes to:** daemon code, `Netclaw.Configuration` (including `NetclawPaths`), channel runtime, provider plugins, security/ACL, persistence schemas, OpenSpec specs (other than the new `demo-apphost` capability spec this change introduces).

**Dependencies (new):** Docker (user prerequisite); `Aspire.Hosting.*` 9.x; `CommunityToolkit.Aspire.Hosting.Ollama`. Verify Aspire 9.x package compatibility with .NET 10 SDK in the design phase; fall back to AppHost-only `net9.0` TFM if required while the rest of the repo stays `net10.0`.

**Operational impact:**
- Cold first-run: pulls `mattermost-preview` image (~600MB) and `qwen3.5:2b-q4_K_M` (~2GB) — documented in README with expected timings.
- Subsequent runs: model cached in a named Docker volume, sub-30s warm boot.
- Resource footprint: ~4GB RAM at idle (Ollama dominates); CPU inference adds ~10–60s per bot reply.
- Cleanup: `docker volume rm` + `rm -rf samples/Netclaw.Demo.AppHost/.demo-home/` returns to a clean state.

**Security impact:**
- `ExposureMode=Local` explicit on the demo daemon — preserves loopback-only auth, `LoopbackAuthenticationHandler` continues to gate operator identity.
- `CallbackUrl` deliberately left **unset** so Mattermost interactive button callbacks fall back to text-reply mode (no inbound HTTP exposure to Mattermost container).
- `NETCLAW_HOME` isolates the demo daemon's SQLite DB, encryption keys, secrets, identity files, and config from any host-installed NetClaw on the same machine — proven pattern (smoke + eval rigs use it daily).
- Demo runs under `Security.StrictDefaults=true` with no seeded `netclaw.json`; any demo-specific behavior is wired via explicit env vars so the default-deny posture stays intact.
- Demo Mattermost admin credentials printed to console on first boot (not committed to repo); test user credentials similarly ephemeral.
- No new secrets-handling surface; existing `SensitiveString` + `ISecretsProtector` pipeline applies to the bot token Aspire injects.

**Deferred (explicit non-goals):**
- SearXNG / Playwright MCP wiring (phase 7+).
- Mattermost interactive button callbacks (requires NetClaw HTTP reachable from container; deferred with the broader containerized-daemon discussion).
- Retrofitting `Netclaw.ServiceDefaults.AddServiceDefaults()` onto the production daemon (separate observability PR).
- Production-grade Mattermost (TE image + Postgres). Preview image is acceptable for a demo.
- Containerizing the demo daemon. Investigated; blocked by `ExposureModeValidationService` + `LoopbackAuthenticationHandler` interactions documented in the design artifact.
