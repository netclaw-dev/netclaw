## 1. Phase 1 — Foundations

- [ ] 1.1 Add central package version pins in `Directory.Packages.props` for `Aspire.Hosting.AppHost`, `Aspire.AppHost.Sdk`, `Aspire.Hosting.Testing`, and `CommunityToolkit.Aspire.Hosting.Ollama`. Verify Aspire 9.x compatibility with the .NET 10 SDK; if needed, pin the AppHost TFM to `net9.0` while the rest of the repo stays `net10.0` (record the decision in the demo README and design's Open Questions if it has to change).
- [ ] 1.2 Create `src/Netclaw.ServiceDefaults/Netclaw.ServiceDefaults.csproj` with `<IsAspireSharedProject>true</IsAspireSharedProject>`, OpenTelemetry registration, health-check + `/health` + `/alive` endpoints, `AddStandardResilienceHandler`, and an `AddServiceDefaults()` extension method. Per `dotnet-skills:aspire-service-defaults`. Do NOT call `AddServiceDefaults()` from `Netclaw.Daemon/Program.cs` in this PR.
- [ ] 1.3 Create `samples/` top-level folder. Add `samples/Netclaw.Demo.AppHost/.demo-home/` to repo `.gitignore`.
- [ ] 1.4 Create `samples/Netclaw.Demo.AppHost/Netclaw.Demo.AppHost.csproj` (`Microsoft.NET.Sdk` + `Aspire.AppHost.Sdk`, `<IsAspireHost>true</IsAspireHost>`, package refs: `Aspire.Hosting.AppHost`, `CommunityToolkit.Aspire.Hosting.Ollama`). Add minimal `Program.cs` that calls `DistributedApplication.CreateBuilder(args)` and `Build().Run()`.
- [ ] 1.5 Register the new ServiceDefaults and AppHost projects in `Netclaw.slnx` under a new `/samples/` solution folder.
- [ ] 1.6 Wire the daemon as a project resource in `samples/Netclaw.Demo.AppHost/Program.cs`: `builder.AddProject<Projects.Netclaw_Daemon>("daemon")` with `.WithEnvironment(...)` for `NETCLAW_HOME=<abs-path-to>/samples/Netclaw.Demo.AppHost/.demo-home/.netclaw`, `NETCLAW_Daemon__Port=5299`, `NETCLAW_Daemon__Host=127.0.0.1`, `NETCLAW_Daemon__ExposureMode=local`. Configure the daemon's HTTP endpoint for Aspire health probes against `/api/health/ready`.
- [ ] 1.7 Verify Phase 1 end-to-end: `dotnet build` clean, `dotnet run --project samples/Netclaw.Demo.AppHost` launches the Aspire dashboard, the daemon resource transitions to healthy, and `samples/Netclaw.Demo.AppHost/.demo-home/.netclaw/` is populated with daemon state (sqlite db, config, identity, logs).

## 2. Phase 1.5 — Aspire MCP integration

- [ ] 2.1 Verify the Aspire dashboard MCP server surface against the installed Aspire version (CLI command `aspire mcp` vs `builder.AddMcpServer()` API or equivalent). Record findings in the demo README's "Driving the demo from Claude Code" section.
- [ ] 2.2 Register the Aspire MCP server at the repo root via the repo's MCP configuration file (e.g., `.mcp.json` or equivalent). Document the connection command.
- [ ] 2.3 Author the "Driving the demo from Claude Code" section in `samples/Netclaw.Demo.AppHost/README.md`.
- [ ] 2.4 Verify Phase 1.5: from a Claude Code session, connect to the registered MCP server while the AppHost is running, enumerate resources, fetch recent log lines from the daemon, and hit `/api/health/ready` via the agent — all without manual human intervention.

## 3. Phase 2 — Mattermost orchestration + bootstrapper extraction

- [ ] 3.1 Create `src/Netclaw.Channels.Mattermost.Bootstrap/Netclaw.Channels.Mattermost.Bootstrap.csproj` (library, TFM net10.0). Public API: `MattermostBootstrapper.SeedAsync(Uri serverUrl, BootstrapOptions opts, CancellationToken ct)` returning `BootstrapResult { ServerUrl, BotUserId, BotToken, TeamId, DefaultChannelId }`. Include readiness polling logic (mirrors `MattermostFixture.WaitUntilApiReady`).
- [ ] 3.2 Move the REST seeding sequence currently inlined in `src/Netclaw.Channels.Mattermost.IntegrationTests/MattermostFixture.cs:196-260` (`CreateUserAsync`, `LoginAsync`, `CreateTeamAsync`, `CreateBotAsync`, channel + membership setup) into the new library.
- [ ] 3.3 Refactor `MattermostFixture.cs` to delegate to `MattermostBootstrapper.SeedAsync`. Run the existing Mattermost integration tests; they must continue to pass with no logic change.
- [ ] 3.4 Add the Mattermost container resource in AppHost `Program.cs`: `mattermost/mattermost-preview` image with env vars `MM_SERVICESETTINGS_ENABLEBOTACCOUNTCREATION=true`, `MM_SERVICESETTINGS_ENABLEUSERACCESSTOKENS=true`, `MM_TEAMSETTINGS_ENABLEOPENSERVER=true`, `MM_SERVICESETTINGS_ENABLETESTING=true`, expose `:8065` as a named HTTP endpoint `web`.
- [ ] 3.5 Create `samples/Netclaw.Demo.Bootstrap/Netclaw.Demo.Bootstrap.csproj` (console app) that takes Mattermost server URL via env var, calls `MattermostBootstrapper.SeedAsync`, and emits `BotToken`, `DefaultChannelId`, `ServerUrl` as Aspire-readable outputs (stdout JSON or equivalent). Decide between executable-project resource and `IDistributedApplicationLifecycleHook` per the design's Decision 6; default to executable project, escalate only if the project-resource path is fragile.
- [ ] 3.6 Wire daemon env vars from bootstrap outputs in AppHost: `NETCLAW_Mattermost__Enabled=true`, `__ServerUrl`, `__BotToken`, `__DefaultChannelId`, `__MentionOnly=false`, `__AllowDirectMessages=true`. Explicitly leave `NETCLAW_Mattermost__CallbackUrl` unset.
- [ ] 3.7 Add `.WaitFor(mattermost).WaitFor(bootstrap)` on the daemon resource so Phase 2's ordering requirement holds.
- [ ] 3.8 Verify Phase 2 via Aspire MCP: confirm Mattermost healthy, read bootstrap outputs, tail daemon log for the `MattermostNetGatewayClient` connect line, then POST a message to the seeded channel via Mattermost REST as the seeded test user and observe a message-received log event in the daemon.
- [ ] 3.9 Decide and document the bootstrap-idempotency policy: re-run seeding on every AppHost start vs. detect prior seed via a sentinel under `.demo-home/`. Resolve design's Open Question #4.

## 4. Phase 3 — Ollama + qwen3:4b

- [ ] 4.1 Confirm the package reference for `CommunityToolkit.Aspire.Hosting.Ollama` resolves; add the using and `.AddOllama("ollama").AddModel("qwen3:4b").WithDataVolume()` to AppHost `Program.cs`.
- [ ] 4.2 Inspect `Netclaw.Configuration` / `ProviderEntry` / `DaemonConfig` to confirm the exact env-var binding shape for the providers array and default provider id. Resolve the placeholder noted in the design (`NETCLAW_Providers__0__*` / `NETCLAW_DefaultProviderId`) against the real schema.
- [ ] 4.3 Wire daemon env vars in AppHost: provider id (`ollama`), kind, endpoint from the Aspire-injected Ollama endpoint, `ModelId=qwen3:4b`, and set the daemon's default provider id.
- [ ] 4.4 Add `.WaitFor(ollama)` on the daemon resource.
- [ ] 4.5 Verify Phase 3 via Aspire MCP: confirm Ollama healthy, agent calls Ollama's `/api/tags` and sees `qwen3:4b`, agent posts "say hi" in the seeded Mattermost channel via REST, observes a non-empty bot reply within ~60s on CPU.

## 5. Phase 4 — Demo defaults, ACL/grants, README, skill update

- [ ] 5.1 Design the seeded `netclaw.json` ACL/grant set. Read `Netclaw.Security` defaults; pick the smallest grant set that allows interesting tool behavior without bypassing policy or using wildcards. Document the chosen grants in the demo README's "What this demo lets the agent do" section. Resolve design's Open Question #1.
- [ ] 5.2 Have the AppHost write the seeded `netclaw.json` to a known path on each startup and mount it into the daemon via the `--config` CLI arg (or equivalent config path env var) so `ConfigSchemaDoctorCheck` validates it.
- [ ] 5.3 Pre-resolve any `~/foo` literals in the seeded `netclaw.json` to absolute paths under `<demoHome>`. Reference `PathExpansion.cs:43` rationale in a brief code comment.
- [ ] 5.4 Author `samples/Netclaw.Demo.AppHost/README.md`: prerequisites (Docker, .NET SDK), launch command, default credentials surfaced via console, first-conversation walkthrough, troubleshooting (cold-pull duration, ports 8065/11434/5299, CPU-only latency), agent-driven verification path, deprecation note for `mattermost-preview`.
- [ ] 5.5 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`: add a "Demo AppHost" section pointing operators at the kick-the-tires path; bump `metadata.version` per the System Skills Sync Rule.
- [ ] 5.6 Verify Phase 4: cold boot (delete `.demo-home/`, `docker volume rm` Ollama and Mattermost containers/volumes, `docker rm -f` lingering containers), measure end-to-end time to first bot reply, capture in the README.

## 6. Phase 5 — Aspire integration test

- [ ] 6.1 Create `samples/Netclaw.Demo.AppHost.IntegrationTests/Netclaw.Demo.AppHost.IntegrationTests.csproj` (xUnit + `Aspire.Hosting.Testing`, ref to `Projects.Netclaw_Demo_AppHost`).
- [ ] 6.2 Add a `[ModuleInitializer]` setting `DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false` per `dotnet-skills:aspire-integration-testing` to avoid Linux inotify exhaustion.
- [ ] 6.3 Implement the single end-to-end test: `DistributedApplicationTestingBuilder.CreateAsync<Projects.Netclaw_Demo_AppHost>()`, `WaitForResourceHealthyAsync` for all four resources (daemon, mattermost, ollama, bootstrap), POST a message via Mattermost REST as the seeded test user, poll the channel for a non-empty bot reply within 90s.
- [ ] 6.4 Tag the test class/method with `[Trait("Category", "SlowSmoke")]` so it does not run on every `dotnet test` invocation.
- [ ] 6.5 Document the test in `TOOLING.md` under the "Interactive CLI Smoke Tests" section: how to invoke, expected duration, prerequisites.
- [ ] 6.6 Verify Phase 5: `dotnet test --filter Category=SlowSmoke` passes locally on a machine with Docker; cold-cache run completes within the documented timeout (~10 min target).

## 7. Phase 6 — Repo-wide finishing + quality gates

- [ ] 7.1 Update top-level `README.md` with a short pointer to the demo and its README.
- [ ] 7.2 Update `PROJECT_CONTEXT.md` to mention the demo path under a "Try it out" / equivalent section.
- [ ] 7.3 Add a `.github/workflows/` (or extend an existing workflow) `workflow_dispatch` job that runs the demo integration test on demand for reviewers; do NOT enable it on push.
- [ ] 7.4 Run `dotnet slopwatch analyze`; resolve any new violations or document a baseline justification.
- [ ] 7.5 Run `./scripts/Add-FileHeaders.ps1 -Verify`; add headers as needed.
- [ ] 7.6 Run `./scripts/smoke/run-smoke.sh light` to confirm no regression in existing smoke tapes.
- [ ] 7.7 Run `/opsx-verify netclaw-demo-apphost` to confirm implementation matches change artifacts.
- [ ] 7.8 Open the PR via `gh pr create` against `dev`; link this OpenSpec change in the PR body; include a "How to demo" section pointing reviewers at the launch command and expected first-conversation behavior.
