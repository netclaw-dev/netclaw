## ADDED Requirements

### Requirement: Single-command launch

The system SHALL bring up Mattermost, Ollama, and the NetClaw daemon as a unified orchestrated demo from a single `dotnet run` against `samples/Netclaw.Demo.AppHost`, with no operator-supplied credentials, API keys, or external infrastructure.

#### Scenario: Cold launch reaches healthy

- **GIVEN** an empty `.demo-home/` directory and no pre-cached Ollama Docker volume
- **WHEN** the operator runs `dotnet run --project samples/Netclaw.Demo.AppHost`
- **THEN** the Aspire dashboard becomes reachable on its local URL
- **AND** the `mattermost`, `ollama`, `ollama-model`, and `daemon` resources each transition to a healthy state within their documented timeouts
- **AND** the seeded Mattermost admin and test-user credentials are printed to console (not committed to the repo)

#### Scenario: Warm launch is fast

- **GIVEN** Ollama's model volume already contains `qwen3.5:2b-q4_K_M` and `.demo-home/` already contains the daemon's SQLite database from a previous run
- **WHEN** the operator runs the AppHost
- **THEN** all resources reach healthy in materially less time than the cold launch
- **AND** no model pull occurs

### Requirement: Daemon state sandboxed via NETCLAW_HOME

The AppHost SHALL set `NETCLAW_HOME` on the demo daemon resource so that the daemon's SQLite database, encryption keys, secrets, identity files, workspaces, and logs live under a known per-demo directory and are isolated from any host-installed NetClaw at `~/.netclaw/`.

#### Scenario: Demo writes only inside its sandbox

- **GIVEN** the operator has a separate host-installed NetClaw with existing state under `~/.netclaw/`
- **WHEN** the operator runs the demo AppHost end-to-end (boot, post a message, receive a reply, shut down)
- **THEN** all daemon-written files for the demo are located under the configured `<demoHome>/.netclaw/` path
- **AND** `~/.netclaw/netclaw.db`, `~/.netclaw/keys/`, `~/.netclaw/config/secrets.json`, and `~/.netclaw/logs/daemon.log` are byte-identical (size + mtime) to their pre-demo state

### Requirement: Daemon binds loopback on a non-default demo port

The AppHost SHALL set `NETCLAW_Daemon__Port=5299`, `NETCLAW_Daemon__Host=127.0.0.1`, and `NETCLAW_Daemon__ExposureMode=local` on the demo daemon resource so that NetClaw's existing security model (`ExposureModeValidationService`, `LoopbackAuthenticationHandler`) is honored without modification.

#### Scenario: Loopback bind and exposure validation pass

- **WHEN** the demo daemon starts
- **THEN** it binds `127.0.0.1:5299` only
- **AND** `ExposureModeValidationService` does not throw at startup
- **AND** the daemon grants `Operator`/`LocalProcess` claims only to caller connections from `IPAddress.IsLoopback`

#### Scenario: No collision with host daemon on default port

- **GIVEN** a host-installed NetClaw daemon is already bound to `127.0.0.1:5199`
- **WHEN** the operator launches the demo AppHost
- **THEN** the demo daemon binds `127.0.0.1:5299` successfully
- **AND** the host daemon at `127.0.0.1:5199` remains uninterrupted
- **AND** neither daemon's lock file is contended

### Requirement: Mattermost credentials seeded before daemon starts

The AppHost SHALL ensure that an admin user, team, bot user, bot access token, and default channel exist in the Mattermost container before the daemon resource is allowed to start. The daemon resource SHALL receive `NETCLAW_Mattermost__Enabled`, `__ServerUrl`, `__BotToken`, and `__DefaultChannelId` as environment variables sourced from the AppHost's bootstrap sequence.

#### Scenario: Bootstrap orders correctly on cold start

- **WHEN** the AppHost starts cold
- **THEN** Mattermost reaches HTTP-ready (responds 200 to `/api/v4/system/ping` and 401 to `/api/v4/users/me` while unauthenticated)
- **AND** the AppHost bootstrap sequence creates admin via `POST /api/v4/users`, logs in, creates the team, creates the bot via `POST /api/v4/bots`, generates the bot access token via `POST /api/v4/users/{botUserId}/tokens`, creates the default channel, and adds the bot to the team and channel
- **AND** the daemon resource transitions to "starting" only after the bootstrap sequence completes
- **AND** the daemon process observes a non-empty `NETCLAW_Mattermost__BotToken` env var before its first attempt to connect to Mattermost

#### Scenario: Bootstrap failure is surfaced

- **WHEN** the bootstrap sequence cannot create the bot (e.g., signup disabled, network unreachable)
- **THEN** the daemon resource does NOT start
- **AND** the AppHost surfaces the bootstrap failure with the underlying error

### Requirement: Ollama model pulled and reachable before daemon starts

The AppHost SHALL pull `qwen3.5:2b-q4_K_M` into the Ollama container and SHALL NOT allow the daemon resource to start until the model is queryable via Ollama's `GET /api/tags`. The daemon resource SHALL receive `NETCLAW_Providers__ollama__*` and `NETCLAW_Models__Main__*` environment variables identifying Ollama as the default LLM provider.

#### Scenario: Cold start pulls and waits

- **GIVEN** no cached Ollama Docker volume
- **WHEN** the AppHost starts
- **THEN** the Ollama container pulls `qwen3.5:2b-q4_K_M`
- **AND** `GET /api/tags` from inside the AppHost's view returns the model
- **AND** the daemon resource starts only after this completes
- **AND** the daemon's effective configuration shows the default provider id as `ollama` and the configured model id as `qwen3.5:2b-q4_K_M`

#### Scenario: Warm start skips pull

- **GIVEN** a cached Ollama volume already contains `qwen3.5:2b-q4_K_M`
- **WHEN** the AppHost starts
- **THEN** no model pull occurs
- **AND** the daemon starts as soon as Ollama is HTTP-ready

### Requirement: End-to-end Mattermost conversation produces a bot reply

When the demo is healthy, posting a message in the seeded default Mattermost channel SHALL produce a bot reply from the NetClaw daemon delivered to the same channel.

#### Scenario: First conversation succeeds

- **GIVEN** the demo AppHost reports all resources healthy
- **WHEN** a message is posted in the seeded default channel (either via the Mattermost web UI as the seeded admin, or via Mattermost's REST `POST /api/v4/posts` as the seeded test user)
- **THEN** the daemon emits a session-message log event for the post
- **AND** a non-empty reply is visible in the same channel from the seeded bot user within the documented timeout for the selected profile and hardware class

### Requirement: Mattermost interactive button approvals are disabled

The demo SHALL leave `NETCLAW_Mattermost__CallbackUrl` unset so that Mattermost interactive button approvals fall back to text-reply mode, avoiding any inbound HTTP exposure from the Mattermost container to the host daemon (which would conflict with `ExposureMode.Local`).

#### Scenario: Approval prompt renders as text

- **GIVEN** the demo's active approval policy requires approval for a particular tool category per `Netclaw.Security` defaults
- **WHEN** the daemon emits an approval request to Mattermost
- **THEN** the prompt arrives in the channel as plain text (no interactive buttons)
- **AND** the demo README documents the text-reply approval syntax the operator must use

### Requirement: Seeded demo configuration preserves default-deny ACL

The configuration shipped with the demo — whether provided via env vars or future config files — SHALL preserve NetClaw's default-deny ACL/grants policy at runtime. Any tool grants enabled for the demo SHALL be explicitly named (no wildcards bypassing policy) and documented in the demo README.

#### Scenario: Demo config validates cleanly

- **WHEN** the daemon starts under the AppHost-provided demo configuration
- **THEN** startup validation succeeds with no `Doctor` violations
- **AND** the daemon starts without disabling any security gate
- **AND** the set of granted tool categories is documented in `samples/Netclaw.Demo.AppHost/README.md`

### Requirement: Aspire MCP integration enables agent-driven verification

The repo SHALL register an MCP server (Aspire dashboard MCP) such that a Claude Code or equivalent MCP-aware agent connected to the repo can enumerate the demo's resources, fetch logs, query health, and trigger resource commands while the demo is running.

#### Scenario: Agent introspects the running demo

- **GIVEN** the demo AppHost is running
- **AND** an MCP-aware agent is connected to the Aspire MCP server registered at the repo root
- **WHEN** the agent invokes the MCP tools to list resources, fetch logs, and check health
- **THEN** the agent observes the `daemon`, `mattermost`, `ollama`, and `ollama-model` resources
- **AND** the agent can retrieve recent log lines from each
- **AND** the agent can query the daemon's `/api/health/ready` and receive HTTP 200

### Requirement: Integration test gates the demo at PR time on demand

The repo SHALL include an Aspire integration test (`samples/Netclaw.Demo.AppHost.IntegrationTests`) that boots the demo AppHost via `DistributedApplicationTestingBuilder`, waits for all resources healthy, posts a Mattermost message via REST, and best-effort waits for the bot reply within a documented timeout. The test SHALL be categorized so it does not run on every push but can be invoked locally and via a manual CI workflow_dispatch job.

#### Scenario: Slow smoke test green locally

- **WHEN** an operator runs `dotnet test --filter Category=SlowSmoke` against a freshly built repo on a machine with Docker available
- **THEN** the test boots the AppHost via `DistributedApplicationTestingBuilder.CreateAsync<Projects.Netclaw_Demo_AppHost>()`
- **AND** waits for all resources to be reported healthy
- **AND** posts a message via the Mattermost REST API as the seeded test user
- **AND** polls the channel for a non-empty bot reply within the configured timeout and emits the documented latency marker if CPU-only inference does not finish in that window
- **AND** tears the AppHost down cleanly with no leaked containers or volumes (volumes are removed if the test created them; reused volumes persist)

#### Scenario: Test does not run by default

- **WHEN** `dotnet test` is run with no category filter
- **THEN** the demo integration test is excluded from the run
- **AND** standard CI pushes do not invoke this test

### Requirement: Demo state lives in operator-resettable locations

All persistent runtime state for the demo SHALL live in known, named, operator-resettable locations: NetClaw daemon state under `samples/Netclaw.Demo.AppHost/.demo-home/`, Ollama model cache in a named Docker volume, Mattermost state in an ephemeral container layer (no volume).

#### Scenario: Clean reset returns to first-run state

- **GIVEN** the demo has been run at least once
- **WHEN** the operator runs `rm -rf samples/Netclaw.Demo.AppHost/.demo-home/` and `docker volume rm <ollama-volume>` and removes any lingering containers
- **AND** runs the AppHost again
- **THEN** the demo cold-starts as if for the first time, including model pull and Mattermost seeding
- **AND** no behavior carries over from the previous run

#### Scenario: Demo state is gitignored

- **GIVEN** the operator has run the demo at least once and `.demo-home/` is populated
- **WHEN** the operator runs `git status` at the repo root
- **THEN** no files under `.demo-home/` appear as untracked or modified
