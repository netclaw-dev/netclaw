# Netclaw Demo AppHost

A self-contained .NET Aspire demo that orchestrates NetClaw end-to-end on
your laptop. The goal: `dotnet run` and chat with a real bot in Mattermost
backed by a real local LLM — no Slack workspace, no API keys, no external
accounts.

## What you get

One `dotnet run` brings up four resources:

- **mattermost** — `mattermost/mattermost-preview` container with admin
  user, team, bot user with personal access token, default channel, and
  a non-admin test user all created automatically.
- **ollama** — Ollama container with `qwen3.5:2b-q4_K_M` pulled on first
  run (~2GB; cached in a named Docker volume thereafter).
- **ollama-model** — the `qwen3.5:2b-q4_K_M` model resource; the daemon
  waits for it before starting its first inference.
- **daemon** — the NetClaw daemon as an Aspire project resource, running
  on the host (not containerized — see the
  [security model section](#why-the-daemon-isnt-containerized) below),
  sandboxed via `NETCLAW_HOME` to a `.demo-home/.netclaw/` directory next
  to this README.

## Prerequisites

- .NET 10 SDK (matching `global.json` at the repo root).
- Docker. Linux is the smoothest experience; Docker Desktop on macOS and
  Windows works but Aspire's container-network model is rougher on those
  platforms.
- ~4GB of disk for the Mattermost preview image + Ollama image +
  `qwen3.5:2b-q4_K_M` weights.
- Ideally a GPU (NVIDIA or AMD via ROCm) — see
  [latency expectations](#latency-expectations) below. The demo runs on
  CPU-only too; it's just slow.

## Launch

From the repo root:

```bash
dotnet run --project samples/Netclaw.Demo.AppHost
```

That launches the `fast` profile by default. It keeps the seeded Mattermost
channel on the `public` audience, caps tool loops aggressively, tunes Ollama
for single-user local inference, disables Ollama thinking mode, and prewarms
the model before the daemon starts. The goal is the fastest possible first
reply on a fresh clone while still exercising the full Aspire-orchestrated
stack.

If you want the heavier tool-rich demo path instead, opt into the `full`
profile:

```bash
NETCLAW_DEMO_PROFILE=full dotnet run --project samples/Netclaw.Demo.AppHost
```

To experiment with a different Ollama tag without editing code:

```bash
NETCLAW_DEMO_MODEL_ID=qwen3.5:4b-q4_K_M dotnet run --project samples/Netclaw.Demo.AppHost
```

On first run you'll see:

1. Mattermost image pull (~1GB, one time).
2. Ollama image pull (~1GB, one time).
3. `qwen3.5:2b-q4_K_M` model pull (~2GB, one time — persisted in a Docker volume).
4. Mattermost startup + bootstrap REST sequence (admin, team, bot, token,
   channel, test user).
5. Daemon process startup, Ollama prewarm, and Mattermost WebSocket connect.

Subsequent runs skip 1-3 and reach "all resources healthy" in ~25-30
seconds (measured: 26s on a Linux VM, warm Docker + Ollama volume cache,
no GPU).

The Aspire dashboard listens on <http://localhost:15294>. Open it to see
each resource's state, env vars, endpoint URLs, and logs.

## First conversation

The bootstrap uses fixed default credentials (these are demo-only and
already public in this repo — they're not secrets):

| Account | Username | Password |
|---|---|---|
| Admin | `admin` | `Admin1234!` |
| Test user | `testuser` | `TestUser1234!` |
| Bot | `testbot` | (no password — uses access token) |

The Mattermost web UI URL appears in the Aspire dashboard under the
`mattermost` resource's `web` endpoint (a port like `http://localhost:38977`,
allocated dynamically). Open it, log in as `testuser`, navigate to the
`test-team` team and the `test-channel` channel, and post a message
mentioning `@testbot`. The bot will reply.

On the default `fast` profile, the seeded channel stays on the `public`
audience. That keeps the prompt lean and the visible tool surface much smaller
than the personal-audience path. If you want to demo the richer
personal-audience behavior, rerun with `NETCLAW_DEMO_PROFILE=full`.

For a quicker test from a shell:

```bash
SERVER=$(curl -sf -m 5 http://localhost:15294/.../mattermost-endpoint)  # or read from dashboard
TOKEN=$(curl -s -i -X POST "$SERVER/api/v4/users/login" \
  -H 'Content-Type: application/json' \
  -d '{"login_id":"testuser","password":"TestUser1234!"}' | grep -i '^Token:' | awk '{print $2}' | tr -d '\r')
# Look up team + channel IDs, then:
curl -s -X POST "$SERVER/api/v4/posts" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"channel_id":"<CHANNEL_ID>","message":"hello @testbot"}'
```

## Latency expectations

`qwen3.5:2b-q4_K_M` is the default because it is the smallest Qwen 3.5 Ollama
tag that still looks like a reasonable bet for tool-calling demos without
pulling the much heavier 4B and 9B variants. It is materially smaller than the
old `qwen3:4b`, but CPU-only hosts are still hardware-bound. Roughly:

| Profile | Intended use | CPU-only behavior |
|---|---|---|
| `fast` (default) | Kick the tires, quick first reply | Usually much faster than the old demo path; still hardware-bound |
| `full` | Heavier tool-rich demo | Can still drift into multi-minute turns on CPU |

On hardware:

| Hardware | First reply latency |
|---|---|
| Modern NVIDIA GPU (≥ 8GB VRAM) | 5-30 seconds |
| AMD GPU via ROCm (≥ 8GB VRAM) | 10-45 seconds |
| Pure CPU, 8+ cores | **several minutes** |
| Pure CPU, 4 cores or VM | **5-15+ minutes** |

To opt into GPU acceleration, edit `Program.cs` and add `.WithGPUSupport(...)`:

```csharp
var ollama = builder.AddOllama("ollama")
    .WithDataVolume()
    .WithGPUSupport(OllamaGpuVendor.Nvidia); // or .Amd
var qwen = ollama.AddModel("ollama-model", "qwen3.5:2b-q4_K_M");
```

This requires the NVIDIA Container Toolkit (Linux) or equivalent ROCm
setup on the host. See the
[CommunityToolkit Aspire Ollama docs](https://github.com/CommunityToolkit/Aspire)
for current setup details.

## Driving the demo from Claude Code (Aspire MCP)

The Aspire CLI ships an MCP server that lets an LLM agent observe and
control the running AppHost. With Claude Code, you can drive the demo
end-to-end without clicking through the dashboard yourself.

If you don't already have the Aspire CLI installed, the cleanest install
path is via the .NET tool:

```bash
dotnet tool install --global Aspire.Cli
# or upgrade an existing dotnet-tool install
dotnet tool update --global Aspire.Cli
```

The CLI version must match (or be newer than) the AppHost's
`Aspire.Hosting.AppHost` pin in `Directory.Packages.props`; otherwise
`aspire mcp` may fail to detect the running AppHost.

One-time setup (run interactively in your terminal, not via Claude Code —
the init flow asks which agent integrations to wire up):

```bash
aspire mcp init
```

That registers the Aspire MCP server in `.mcp.json` (Claude Code) and
`opencode.jsonc` (OpenCode). Restart your agent so it picks up the new
MCP server.

With the AppHost running, an agent has tools like:

- `mcp__aspire__list_apphosts` — discover running AppHosts in the workspace.
- `mcp__aspire__select_apphost` — focus on a specific AppHost.
- `mcp__aspire__list_resources` — enumerate resources, state, env vars,
  dashboard links.
- `mcp__aspire__list_console_logs` — fetch process launch + DCP-level logs
  for a named resource (note: container resources surface launch info;
  app-level file loggers like NetClaw's daemon log are on disk).
- `mcp__aspire__execute_resource_command` — `resource-stop`,
  `resource-restart`, etc.
- `mcp__aspire__list_structured_logs`, `list_traces` — OpenTelemetry-backed
  observability.

### Running the test

```bash
NETCLAW_RUN_DEMO_SMOKE=1 \
  dotnet test samples/Netclaw.Demo.AppHost.IntegrationTests \
    --filter Category=SlowSmoke
```

The test self-skips on a bare `dotnet test` unless
`NETCLAW_RUN_DEMO_SMOKE=1` is set. Override
`NETCLAW_DEMO_TEST_REPLY_TIMEOUT_SECONDS` to extend the bot-reply
window on CPU-only hosts.

A typical agent verification flow:

```text
list_apphosts          # discover the demo
select_apphost         # focus on it
list_resources         # confirm mattermost + ollama + ollama-model + daemon all Running
list_console_logs      # check resource launch logs
# then drive Mattermost via REST (post a message, poll the channel for the bot reply)
```

NetClaw's daemon writes to a file logger at
`samples/Netclaw.Demo.AppHost/.demo-home/.netclaw/logs/daemon-YYYY-MM-DD.log`
— tail that for the application-level activity (sessions, LLM calls, tool
invocations).

## State isolation

The AppHost sets `NETCLAW_HOME=<repo>/samples/Netclaw.Demo.AppHost/.demo-home/.netclaw`
on the daemon process. `NetclawPaths` (`src/Netclaw.Configuration/NetclawPaths.cs:113`)
already honors this env var — it's the same knob the smoke harness and the
eval rig use.

To return to a clean slate:

```bash
rm -rf samples/Netclaw.Demo.AppHost/.demo-home/
# Optional: also wipe Ollama's model cache to force a re-pull
docker volume ls | grep ollama | awk '{print $2}' | xargs -r docker volume rm
```

The eight other `SpecialFolder.UserProfile` callsites in NetClaw
(`PathExpansion`, `ExternalSkillsConfig`, `ShellCommandPolicy`,
`DaemonManager`, `UpdateCommand`, `BrowserAutomationRuntimeDetector`,
`IdentityStepViewModel`, `CrashLogWriter`) intentionally read the real
operator home — they're about your real Chrome install, real
`~/.claude/skills`, real CLI install. `NETCLAW_HOME` doesn't redirect them,
and that's by design.

## Security posture

The demo ships with NetClaw's default security configuration — no custom
ACL, no custom grants, no `netclaw.json` of its own. That means:

- `Security.StrictDefaults=true` (the conservative default).
- `Daemon.ExposureMode=local`, daemon bound to `127.0.0.1:5299` (non-default
  port to avoid colliding with any host-installed daemon on 5199).
- The default `fast` profile pins the seeded Mattermost channel to the
  `public` audience so the first turn carries the smallest safe prompt and
  tool surface. `NETCLAW_DEMO_PROFILE=full` restores the heavier
  `personal`-audience behavior.
- `LoopbackAuthenticationHandler` grants `Operator` identity only to
  loopback callers, which the AppHost-launched daemon process honors.
- `Tools.WebFetch.RequireHttps=true` (plain HTTP only allowed for
  loopback hosts).
- No shell execution by default; the bot can chat and use built-in
  tools but cannot run arbitrary shell commands on your machine.
- `Mattermost.CallbackUrl` deliberately **unset** — Mattermost interactive
  button approvals fall back to text-reply mode, avoiding any inbound HTTP
  exposure from the Mattermost container to the host daemon.

If you want to extend the demo to enable tool calls that touch the
filesystem or shell, write a `netclaw.json` under
`.demo-home/.netclaw/config/` with explicit grants and document what you
turned on. **Do not** ship a permissive demo config; the secure-by-default
posture is intentional.

## Why the daemon isn't containerized

NetClaw's daemon enforces a security model that's awkward to satisfy
inside a Docker bridge network: `ExposureModeValidationService` aborts
startup when `ExposureMode=Local` and `Daemon.Host != 127.0.0.1/::1`, and
`LoopbackAuthenticationHandler` grants operator identity only to loopback
source IPs. Running the daemon as a container with `0.0.0.0:5299` would
collide with both checks.

The eval rig (`evals/run-evals.sh`) sidesteps this with
`docker run --network host`, but Aspire's container model doesn't cleanly
express host networking, and `--network host` is Linux-only (degraded on
Mac/Windows Docker Desktop). For the demo we run the daemon as a host
process via `AddProject<>` — fast iteration, no security model
interference, and the `NETCLAW_HOME` env var on the process gives all the
state isolation we actually need.

A future change may introduce an `ExposureMode.Orchestrated` paired with
a shared-secret auth scheme so a containerized daemon makes sense in
production deployments. That's deferred — see
`openspec/changes/netclaw-demo-apphost/design.md` Risk #1.

## Troubleshooting

- **`aspire mcp init` errors with "Interactive input not supported"** — the
  init flow always wants an interactive terminal even with
  `--non-interactive`. Run it in a real shell, not under an agent harness.
- **Port `5299` in use** — another process holds the demo daemon's port.
  `ss -tlnp | grep 5299` to identify it. Either stop the conflicting
  process or change `NETCLAW_Daemon__Port` in `Program.cs`.
- **`.demo-home` accumulates state** — that's expected; it's the daemon's
  full state tree. Wipe with
  `rm -rf samples/Netclaw.Demo.AppHost/.demo-home/` for a clean run.
- **Mattermost image pull is slow** — `mattermost/mattermost-preview` is
  ~1GB and currently runs as `linux/amd64` on ARM hosts because upstream
  does not publish an ARM64 manifest. One time only; subsequent runs reuse
  the image.
- **Model pull is slow or interrupted** — Aspire retries automatically.
  Once `qwen3.5:2b-q4_K_M` is in the named volume, subsequent runs skip the pull.
- **You want the old richer demo even if it's slower** — run with
  `NETCLAW_DEMO_PROFILE=full`.
- **Bot reply is taking forever on CPU** — see
  [latency expectations](#latency-expectations) above; opt into GPU
  support if you have hardware available.
- **`mattermost/mattermost-preview` is deprecated upstream** — still
  functional; we use it because it's self-contained (no separate Postgres
  needed). A future change will migrate to
  `mattermost/mattermost-team-edition` + Postgres for a more
  production-shaped demo.
