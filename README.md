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

# Netclaw

Netclaw is an open-source, self-hosted autonomous operations agent that runs
anywhere — from a Raspberry Pi to a cloud VM. Built on top of a minimal
actor-driven session framework called Akka.Agents, Netclaw is designed for
hobbyists, small teams, and businesses who want an AI operations agent with
strong safety defaults and as few moving parts as possible.

Your data stays on your infrastructure. Your agent keeps running when a
provider changes their pricing. You control what gets approved and what runs
autonomously — small models welcome.

Where other agents compete on ecosystem breadth and feature velocity, Netclaw
takes the opposite approach: **simplicity** (a readable codebase with a small
configuration footprint), **security** (audience dispositions and approval gates
from day one, not bolted on after incidents), and **reliability** (curated skill
feeds managed by your organization, not an unaudited public marketplace).

Learn more at **[netclaw.dev](https://netclaw.dev)**.

## How It Works

Netclaw uses a **daemon + thin client** architecture:

- **`netclawd`** — an always-on background daemon that hosts LLM sessions,
  tool execution, and persistence. Start it once and it stays running.
- **`netclaw`** — a lightweight CLI for interactive chat, daemon management,
  and configuration. It connects to the running daemon over a local socket.

You start the daemon, then use the CLI to talk to it. Remote devices can
[pair with the daemon](https://netclaw.dev/guides/pairing-remote-devices/)
over Tailscale or Cloudflare Tunnel for access from anywhere.

## Quick Start

### Prerequisites

- An LLM provider — [Ollama](https://ollama.com/) (local, default),
  [OpenRouter](https://openrouter.ai/), or any OpenAI-compatible endpoint.
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

# Pin a specific version
NETCLAW_VERSION=0.17.1 curl -sSL https://releases.netclaw.dev/install.sh | bash
```

**Windows** (installs to `%LOCALAPPDATA%\Programs\netclaw`):

```powershell
iwr -useb https://releases.netclaw.dev/install.ps1 | iex
```

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

See the [Docker deployment guide](https://netclaw.dev/deployment/docker/) for
volume setup, environment variables, and Docker Compose examples.

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
