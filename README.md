<p align="center">
  <img src="https://raw.githubusercontent.com/netclaw-dev/netclaw-brand/dev/logo/netclaw-horizontal-purple.png" alt="Netclaw" width="400" />
</p>

<p align="center">
  <strong>Run your own agent.</strong><br />
  Simple, secure, reliable agents.
</p>

<p align="center">
  <a href="https://netclaw.dev">Website</a> &middot;
  <a href="https://netclaw.dev/docs">Documentation</a> &middot;
  <a href="https://github.com/netclaw-dev/netclaw/releases">Releases</a> &middot;
  <a href="https://discord.gg/ayqrChDtNs">Discord</a>
</p>

<p align="center">
  <a href="https://github.com/netclaw-dev/netclaw/releases/latest">
    <img src="https://img.shields.io/github/v/release/netclaw-dev/netclaw?style=flat-square&logo=github&label=latest&color=512BD4" alt="Latest Release" />
  </a>
  <a href="https://github.com/netclaw-dev/netclaw/blob/dev/LICENSE">
    <img src="https://img.shields.io/github/license/netclaw-dev/netclaw?style=flat-square&color=512BD4&logo=github" alt="License" />
  </a>
  <a href="https://discord.gg/ayqrChDtNs">
    <img src="https://img.shields.io/discord/1494176300657545318?style=flat-square&color=5865F2&logo=discord&logoColor=white" alt="Discord" />
  </a>
  <a href="https://ghcr.io/netclaw-dev/netclaw">
    <img src="https://img.shields.io/badge/docker-ghcr.io%2Fnetclaw--dev%2Fnetclaw-512BD4?style=flat-square&logo=docker&logoColor=white" alt="Docker Image" />
  </a>
</p>

# Netclaw

Netclaw is an open-source, self-hosted autonomous operations agent that runs
anywhere — from a Raspberry Pi to a cloud VM. Built on **Akka.NET**, the actor
framework from Petabridge, it's designed for anyone who wants an AI operations
agent with strong safety defaults and as few moving parts as possible.

Your data stays on your infrastructure. Your agent keeps running when a
provider changes their pricing. You control what gets approved and what runs
autonomously — small models welcome.

Other agents go for feature breadth and release velocity. We went a different
route: **simplicity** (readable code, minimal config footprint), **security**
(approval gates and audience dispositions built in, not bolted on after
incidents), and **reliability** (curated skill feeds managed by your org, not
an unaudited public marketplace).

Learn more at **[netclaw.dev](https://netclaw.dev)**.

## How It Works

Netclaw runs as a daemon plus a thin CLI:

- **`netclawd`** — the daemon. It hosts LLM sessions, runs tools, and handles
  persistence. Start it once and it stays up.
- **`netclaw`** — the CLI. Talk to the daemon, manage config, run commands.
  It connects over a local socket.

Start the daemon, then use the CLI. Remote devices can
[pair with the daemon](https://netclaw.dev/guides/pairing-remote-devices/)
over Tailscale or Cloudflare Tunnel.

## Quick Start

### Just want to kick the tires?

`samples/Netclaw.Demo.AppHost` is a self-contained .NET Aspire demo that
brings up NetClaw + Mattermost + Ollama with seeded credentials in a
single command — no Slack workspace, no API keys, no external accounts.
See [`samples/Netclaw.Demo.AppHost/README.md`](samples/Netclaw.Demo.AppHost/README.md).

```bash
dotnet run --project samples/Netclaw.Demo.AppHost
```

### Prerequisites

- An LLM provider — [Ollama](https://ollama.com/) (local, default),
  [OpenRouter](https://openrouter.ai/), [DwarfStar/ds4](https://github.com/antirez/ds4)
  (local DeepSeek V4 on Apple Silicon / CUDA), or any OpenAI-compatible endpoint.
  See the full [provider documentation](https://netclaw.dev/configuration/managed-providers/)
  for all supported options.

### Install

**Linux** (installs CLI + daemon to `~/.netclaw/bin`):

```bash
curl -sSL https://releases.netclaw.dev/install.sh | bash
```

```bash
# Install only the CLI or only the daemon
curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- cli
curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- daemon

# Opt into the beta channel (newest prerelease, or latest stable if none)
curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- --channel beta

# Pin a specific version (e.g. a prerelease)
NETCLAW_VERSION=0.17.1 curl -sSL https://releases.netclaw.dev/install.sh | bash
```

**macOS** (Apple Silicon — M1 or later — installs CLI + daemon to `~/.netclaw/bin`):

```bash
curl -sSL https://releases.netclaw.dev/install.sh | bash
```

The `cli` / `daemon` / `NETCLAW_VERSION` options shown above for Linux work the
same way on macOS. Auto-starting the daemon as a background service is not yet
available on macOS ([#1015](https://github.com/netclaw-dev/netclaw/issues/1015))
— run it manually with `netclaw daemon start`.

**Windows** (installs to `%LOCALAPPDATA%\Programs\netclaw`):

```powershell
iwr -useb https://releases.netclaw.dev/install.ps1 | iex
```

The `-Component cli|daemon`, `-Channel beta`, and `-Version` options work the same
way as their Linux counterparts (download the script and run it with the flag).

**Docker** (multi-arch: amd64/arm64):

```bash
docker run -d --name netclawd \
  -p 5199:5199 \
  -v ~/.netclaw:/home/netclaw/.netclaw \
  -e NETCLAW_Daemon__Host=0.0.0.0 \
  -e NETCLAW_Daemon__ExposureMode=reverse-proxy \
  -e NETCLAW_Daemon__TrustedProxies__0=172.16.0.0/12 \
  ghcr.io/netclaw-dev/netclaw:latest
```

Use `ghcr.io/netclaw-dev/netclaw:beta` to track the newest prerelease, or a pinned
tag like `:0.19.0-beta.1`. `:latest` only ever points at the latest stable release.

See the [Docker deployment guide](https://netclaw.dev/deployment/docker/) for
volume setup, environment variables, and Docker Compose examples.

### Beta / prerelease versions

Netclaw publishes opt-in **beta** builds so you can test an upcoming release early.
Stable installs are never affected — the default `curl | sh`, Docker `:latest`, and
the GitHub "Latest" release always point at the newest *stable*. The beta channel
follows the newest prerelease and automatically rolls onto a stable release once it
supersedes the beta.

```bash
# Linux / macOS — newest prerelease (falls back to latest stable if none is open)
curl -sSL https://releases.netclaw.dev/install.sh | bash -s -- --channel beta
```

```powershell
# Windows — download, then run with -Channel beta
iwr -useb https://releases.netclaw.dev/install.ps1 -OutFile install.ps1
./install.ps1 -Channel beta
```

```bash
# Docker — :beta tracks the newest prerelease (:latest stays on stable)
docker pull ghcr.io/netclaw-dev/netclaw:beta
```

To pin an exact build instead of following the channel, name the version directly:
`NETCLAW_VERSION=0.19.0-beta.1` (Linux/macOS), `-Version 0.19.0-beta.1` (Windows), or
the `:0.19.0-beta.1` image tag (Docker).

For the full installation reference (including building from source), see the
[installation docs](https://netclaw.dev/getting-started/installation/).

### Configure

Run the guided setup wizard:

```bash
netclaw init
```

Or create the config manually. The daemon reads layered config from
`~/.netclaw/config/`:

**`~/.netclaw/config/netclaw.json`** — base settings (minimal Ollama example):

```json
{
  "configVersion": 1,
  "Providers": {
    "local-ollama": {
      "Type": "ollama",
      "Endpoint": "http://localhost:11434"
    }
  },
  "Models": {
    "Main": { "Provider": "local-ollama", "ModelId": "qwen3:30b" }
  }
}
```

Credentials are stored encrypted in `~/.netclaw/config/secrets.json`. Use the
CLI to manage them — never edit that file by hand:

```bash
netclaw secrets set Providers.openrouter.ApiKey sk-or-v1-...
netclaw secrets set Slack.BotToken xoxb-...
```

All settings can also be overridden via environment variables using the
`NETCLAW_` prefix with double-underscore separators for nested keys:

```bash
export NETCLAW_Providers__local-ollama__Endpoint=http://localhost:11434
export NETCLAW_Models__Main__ModelId=qwen3:8b
```

For the full configuration reference, see the
[configuration docs](https://netclaw.dev/configuration/managed-providers/).

### Validate

```bash
netclaw doctor          # Check config schema, provider connectivity, secrets
netclaw doctor --fix    # Auto-apply safe fixes
```

### Run

```bash
# Start the daemon (background process)
netclaw daemon start

# Check daemon status
netclaw daemon status

# Interactive chat (connects to running daemon)
netclaw chat

# Single-prompt mode (non-interactive)
netclaw chat -p "What's on my calendar today?"

# Stop the daemon
netclaw daemon stop
```

For the full quickstart walkthrough, see the
[quickstart guide](https://netclaw.dev/getting-started/quickstart/).

## Channels

Netclaw connects to your team's existing communication channels:

- **[Slack](https://netclaw.dev/channels/slack/)** — Socket Mode gateway with per-channel audience controls
- **[Discord](https://netclaw.dev/channels/discord/)** — Guild and DM support

## Deployment

- **[Docker](https://netclaw.dev/deployment/docker/)** — multi-arch images on GHCR (`ghcr.io/netclaw-dev/netclaw`)
- **[systemd](https://netclaw.dev/deployment/systemd/)** — `netclaw daemon install` creates a user-level service
- **[Exposure Modes](https://netclaw.dev/deployment/exposure-modes/)** — local, Tailscale, or Cloudflare Tunnel

## Security

Netclaw is default-deny from the ground up. The daemon requires explicit
configuration before it will execute tools, connect to channels, or accept
remote connections.

- **[Security Model](https://netclaw.dev/security/security-model/)** — audiences, approval gates, tool policies
- **[Hardening Guide](https://netclaw.dev/security/hardening/)** — production lockdown checklist
- **[Secrets Management](https://netclaw.dev/security/secrets/)** — encrypted-at-rest credential storage
- **[Pairing Remote Devices](https://netclaw.dev/guides/pairing-remote-devices/)** — two-sided pairing protocol with rate-limited code exchange

## CLI Reference

Full CLI documentation is available at [netclaw.dev/cli](https://netclaw.dev/cli/overview/).

```
netclaw init                     First-run setup wizard
netclaw chat                     Interactive TUI chat
netclaw chat -p <text>           Headless single-prompt mode
netclaw doctor                   Configuration diagnostics
netclaw daemon start|stop|status Manage the daemon process
netclaw daemon install           Install systemd user service (Linux)
netclaw daemon pair              Generate a pairing code for remote access
netclaw provider                 Manage LLM providers
netclaw model                    Manage model assignments
netclaw mcp                      Manage MCP server profiles
netclaw skill                    Manage skills and skill sources
netclaw reminder                 Manage scheduled reminders
netclaw webhooks                 Manage inbound webhook routes
netclaw secrets set <k> <v>      Manage encrypted secrets
netclaw update                   Check for and install updates
netclaw version                  Show CLI version
```

## Documentation

Visit **[netclaw.dev/docs](https://netclaw.dev/docs)** for the full
documentation, including:

- [Getting Started](https://netclaw.dev/getting-started/installation/) — installation, quickstart, first conversation
- [Configuration](https://netclaw.dev/configuration/managed-providers/) — providers, models, MCP servers, webhooks, reminders
- [Skills](https://netclaw.dev/skills/overview/) — skill system, skill feeds, authoring custom skills
- [Guides](https://netclaw.dev/guides/connecting-slack/) — Slack setup, MCP permissions, remote pairing
- [Architecture](https://netclaw.dev/architecture/overview/) — system design and security model
- [Observability](https://netclaw.dev/observability/health-checks/) — health checks, alerts, OpenTelemetry

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for development workflows, build
instructions, project structure, and contributor tooling.

## License

Netclaw is licensed under the Apache License, Version 2.0.
See `LICENSE` for the full text.

---

Built with care by [Petabridge](https://petabridge.com). Visit
[netclaw.dev](https://netclaw.dev) for documentation, guides, and community
resources.
