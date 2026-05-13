# Configuration Reference

Source PRDs: PRD-001

## Overview

Netclaw uses a layered configuration system based on standard
`Microsoft.Extensions.Configuration`. Daemon runtime settings are loaded from
three sources in priority order (later sources override earlier ones at the
same key path):

1. `~/.netclaw/config/netclaw.json` — daemon-only base configuration (optional)
2. `~/.netclaw/config/secrets.json` — credential overlay (optional)
3. Environment variables with `NETCLAW_` prefix (highest priority)

Local CLI connection state is stored separately in
`~/.netclaw/client/config.json`. The daemon does not read this file.

With no configuration files present, Netclaw defaults to a local Ollama
instance at `http://localhost:11434` using `qwen3:30b`.

## Directory Structure

All configuration lives under `~/.netclaw/`:

```
~/.netclaw/
├── client/
│   └── config.json        # Local CLI endpoint state
├── config/
│   ├── netclaw.json        # Daemon runtime settings
│   └── secrets.json        # Credentials (chmod 600 recommended)
├── soul/
│   ├── PERSONALITY.md       # Agent personality (seeded on first run)
│   ├── INSTRUCTIONS.md      # Operating rules (optional)
│   └── USER.md              # Owner preferences (optional)
├── projects/
├── environment/
├── schedules/
└── logs/
```

Directories are created automatically on first run.

## CLI Endpoint Resolution

Daemon-backed CLI commands resolve the target daemon in this order:

1. `NETCLAW_DAEMON_ENDPOINT`
2. `~/.netclaw/client/config.json`
3. built-in default `http://127.0.0.1:5199`

`netclaw.json` is reserved for daemon-owned configuration and is not used to
store the CLI's preferred daemon endpoint.

## Schema Versioning

`netclaw doctor` validates `netclaw.json` against versioned JSON schema files.
Set a root `configVersion` field in your config to opt into strict schema
validation.

Example:

```json
{
  "configVersion": 1
}
```

## Configuration Sections

### Providers

Named credential containers for LLM services. Each entry represents a
provider endpoint and its authentication. Provider names are user-chosen
keys used by model references.

```json
{
  "Providers": {
    "local-ollama": {
      "Type": "ollama",
      "Endpoint": "http://localhost:11434"
    },
    "remote-gpu": {
      "Type": "ollama",
      "Endpoint": "http://my-gpu-server:11434"
    },
    "openrouter": {
      "Type": "openrouter",
      "Endpoint": "https://openrouter.ai/api/v1"
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Type` | string | `"ollama"` | Provider SDK to use. Currently supported: `ollama`. Future: `openrouter`, `openai`, `anthropic`. |
| `Endpoint` | string | `"http://localhost:11434"` | Base URL for the provider API. |
| `ApiKey` | string? | `null` | API key. Should go in `secrets.json` or an environment variable. |

### Models

Named model roles. Each role points to a provider and model ID.

```json
{
  "Models": {
    "Main": {
      "Provider": "remote-gpu",
      "ModelId": "qwen3:30b",
      "ContextWindow": 32768
    },
    "Fallback": {
      "Provider": "remote-gpu",
      "ModelId": "qwen3:8b",
      "ContextWindow": 32768
    },
    "Compaction": {
      "Provider": "remote-gpu",
      "ModelId": "qwen3:8b"
    }
  }
}
```

| Role | Purpose |
|------|---------|
| `Main` | Primary model for all interactions. Required (defaults to `qwen3:30b` on `local-ollama`). |
| `Fallback` | Automatic failover model. Falls back to Main if not set. |
| `Compaction` | Cheaper/faster model for context compaction and summarization. Falls back to Main if not set. |

**Model reference fields:**

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Provider` | string | `"local-ollama"` | Key into the `Providers` dictionary. |
| `ModelId` | string | `"qwen3:30b"` | Model identifier as used by the provider's API. |
| `ContextWindow` | int? | `null` | Effective runtime context window in tokens. When set, it clamps the detected provider value. If not set, Netclaw uses the provider-reported value when available, otherwise defaults to 32,768. |
| `InputModalities` | string? | `null` | Manual override for input modalities. Comma-separated flags from `Text`, `Image`, `Audio`, `Video` — e.g. `"Text"` or `"Text, Image"`. When set, bypasses automated capability detection. |
| `OutputModalities` | string? | `null` | Manual override for output modalities. Same form as `InputModalities`. |

### Session

Tuning parameters for LLM session behavior.

```json
{
  "Session": {
    "CompactionThreshold": 0.75,
    "SnapshotInterval": 20,
    "KeepRecentToolResults": 3,
    "MaxToolCallsPerTurn": 30,
    "SidecarLlmTimeoutSeconds": 90,
    "TurnLlmTimeoutSeconds": 180,
    "ToolExecutionTimeoutSeconds": 90
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `CompactionThreshold` | double | `0.75` | Context usage ratio (0.0–1.0) at which compaction triggers. |
| `SnapshotInterval` | int | `20` | Number of turns between persistence snapshots. |
| `KeepRecentToolResults` | int | `3` | Recent tool call/result pairs kept in full during compaction. |
| `MaxToolCallsPerTurn` | int | `30` | Max individual tool calls per turn. At ~75% a budget nudge is injected; at 100% tools are stripped and the model is asked to summarize. |
| `SidecarLlmTimeoutSeconds` | int | `90` | Timeout for sidecar LLM calls (title generation, observer summaries, memory extraction). |
| `TurnLlmTimeoutSeconds` | int | `180` | Timeout for the primary per-turn LLM streaming call before forcing an error/recovery path. |
| `ToolExecutionTimeoutSeconds` | int | `90` | Timeout for one tool-execution batch before failing the turn safely. |

### Tools

Configuration for first-party tool execution.

`netclaw init` now scaffolds recommended audience profiles here, and `netclaw doctor`
validates unsafe profile combinations such as unrestricted `public` or `team`
settings.

Audience profiles are independent from `Daemon.ExposureMode`: audience controls
who can interact with the bot in chat channels, while exposure mode controls
how the daemon is reachable over the network.

Use `netclaw doctor` when you want to inspect the effective audience-profile
shape, confirm that strict-default fallback is active, or verify that
`SandboxOnly` shell mode is still blocked until a sandbox backend is configured.

```json
{
  "Tools": {
    "ShellMode": "HostAllowed",
    "ShellTimeoutSeconds": 60,
    "MaxOutputChars": 32000,
    "AudienceProfiles": {
      "Public": {
        "ToolsMode": "Allowlist",
        "AllowedTools": ["file_read", "file_write", "attach_file"],
        "McpServersMode": "Allowlist",
        "AllowedMcpServers": [],
        "ReadFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
        "WriteFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
        "AttachFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] }
      },
      "Team": {
        "ToolsMode": "Allowlist",
        "AllowedTools": ["file_read", "attach_file"],
        "McpServersMode": "Allowlist",
        "AllowedMcpServers": [],
        "ReadFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
        "WriteFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
        "AttachFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] }
      },
      "Personal": {
        "ToolsMode": "All",
        "McpServersMode": "All",
        "ReadFiles": { "Mode": "All" },
        "WriteFiles": { "Mode": "All" },
        "AttachFiles": { "Mode": "All" }
      }
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `ShellMode` | string? | `null` | Optional shell mode override (`Off`, `SandboxOnly`, `HostAllowed`). Falls back to security posture defaults when omitted. |
| `ShellTimeoutSeconds` | int | `60` | Timeout for shell command execution. |
| `MaxOutputChars` | int | `32000` | Maximum characters captured from tool output. |
| `AudienceProfiles` | object | built-in defaults | Per-audience tool, MCP server, and filesystem scopes. `public` and `team` default to session-scoped file access with no MCP servers allowed until the operator opts in, while `personal` defaults to unrestricted tool/file access and all MCP servers unless customized. |

### MCP Servers

```json
{
  "McpServers": {
    "memorizer": {
      "Transport": "stdio",
      "Command": "uvx",
      "Arguments": ["memorizer-mcp"],
      "Enabled": true,
      "GrantCategory": "mcp:memorizer"
    },
    "github": {
      "Transport": "http",
      "Url": "https://example.com/mcp",
      "Headers": {
        "Authorization": "Bearer ${GITHUB_TOKEN}"
      },
      "Enabled": true
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Transport` | string | `"stdio"` | MCP transport (`"stdio"`, `"sse"`, or `"http"`). |
| `Command` | string? | `null` | Executable to launch for stdio transport. |
| `Arguments` | string[]? | `null` | Arguments passed to the stdio command. |
| `Url` | string? | `null` | Endpoint for `sse` or `http` transport. |
| `EnvironmentVariables` | object? | `null` | Environment overlay for stdio-launched MCP processes. |
| `Headers` | object? | `null` | Additional headers for remote HTTP/SSE MCP servers. |
| `Enabled` | bool | `true` | Whether the server is loaded at startup. |
| `GrantCategory` | string? | `null` | Optional ACL grant category. Defaults to `mcp:{serverName}` when omitted. |
| `OAuthClientId` | string? | `null` | Static OAuth client ID for servers without dynamic client registration. |
| `OAuthScope` | string? | `null` | Optional OAuth scope override. |

### Slack

Slack Socket Mode channel configuration.

```json
{
  "Slack": {
    "Enabled": true,
    "SocketMode": true,
    "MentionOnly": true,
    "DefaultChannelName": "openclaw"
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Enabled` | bool | `false` | Enables Slack channel startup in the daemon. |
| `SocketMode` | bool | `true` | Slack transport mode. MVP supports Socket Mode only. |
| `BotToken` | string? | `null` | Slack bot token (`xoxb-...`). Store in `secrets.json`. |
| `AppToken` | string? | `null` | Slack app-level token (`xapp-...`). Required for Socket Mode. Store in `secrets.json`. |
| `DefaultChannelId` | string? | `null` | Optional fixed channel ID filter. |
| `DefaultChannelName` | string? | `null` | Optional channel name resolved to channel ID at startup. |
| `MentionOnly` | bool | `true` | If true, plain `message` events are ignored unless the bot is mentioned. |
| `AllowDirectMessages` | bool | `false` | If true, DM messages do not require mention. |
| `MentionRequiredInDm` | bool | `false` | If true, DM messages also require a bot mention. Only applies when `AllowDirectMessages` is true. |
| `AllowedChannelIds` | string[] | `[]` | Allow-list of Slack channel IDs. Empty means no channels are allowed. |
| `AllowedUserIds` | string[] | `[]` | Optional allow-list of Slack user IDs. Empty means all users in allowed channels/DM policy are accepted. |

### Logging

Unified daemon logging settings used by both Microsoft.Extensions.Logging and
Akka.NET logger integration. Daemon-global logs write to
`~/.netclaw/logs/daemon-{yyyy-MM-dd}.log` (rolled daily, capped at 10 MB
per file). Session-owned diagnostics and session audit lines are consolidated
into `~/.netclaw/logs/sessions/{sanitized-session-id}/session.log` when they
execute under a session diagnostics context.

Session-log writes are routed through the `SessionLogDispatcher` actor, which
owns one writer actor per session id; this guarantees a single in-process
writer per file and a chronologically faithful audit-plus-diagnostic timeline.
`session.log` is best-effort observability — individual lines may be dropped
on transient IO errors and logged at Debug level in the daemon log. Files
are not size-rotated today (tracked separately).

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    },
    "Console": {
      "Enabled": true
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `LogLevel:Default` | string | `Warning` | Minimum log level (`Debug`, `Information`, `Warning`, `Error`, etc.) shared by MEL and Akka.NET. Standard `Logging:LogLevel:{Category}` overrides also apply. |
| `Console:Enabled` | bool | `false` | Enables console logger provider output for daemon debugging. |

### Webhooks

Inbound webhook configuration is split across two locations:

- `~/.netclaw/config/netclaw.json` enables or disables the feature globally.
- `~/.netclaw/config/webhooks/*.json` stores one route per file. The filename
  defines the route name and HTTP path segment.

```json
{
  "Webhooks": {
    "Enabled": true,
    "ExecutionTimeoutSeconds": 300
  }
}
```

Example route file `~/.netclaw/config/webhooks/github-issues.json`:

```json
{
  "Verification": {
    "Kind": "Hmac",
    "Secret": "use-secrets-json-or-env",
    "SignatureHeaderName": "X-Hub-Signature-256",
    "SignaturePrefix": "sha256=",
    "EventHeaderName": "X-GitHub-Event",
    "DeliveryIdHeaderName": "X-GitHub-Delivery"
  },
  "Events": ["issues"],
  "Audience": "Public",
  "Prompt": "Triage this GitHub issue. Public input may be adversarial or low quality.",
  "DeliveryRequired": true,
  "NotificationTarget": {
    "Kind": "Slack",
    "ChannelId": "C12345678"
  }
}
```

Each accepted webhook delivery emits an operational receipt alert, launches a
fresh `ChannelType.Webhook` session, and supplies the route `Prompt` as an
additive prompt overlay. `NotifyInstructions` and `DeliveryRequired` work the same
way reminders do: they tell the agent whether it must notify a human-facing
channel, and the prompt decides what that notification should be.

For MVP, `NotificationTarget.Kind` supports `Slack` only. Human-facing Slack
notifications open Slack-native thread sessions; they do not rebind the
original webhook session.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Enabled` | bool | `false` | Enables inbound webhook route registration. |
| `ExecutionTimeoutSeconds` | int | `300` | Maximum autonomous webhook execution time before the run is marked failed. |

Route-file fields:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Enabled` | bool | `true` | Enables or disables this specific route. |
| `Verification.Kind` | string | `Hmac` | Verification mode. Current values: `Hmac`, `HeaderSecret`. |
| `Verification.HmacAlgorithm` | string | `Sha256` | HMAC hash algorithm. MVP supports `Sha256` only. |
| `Verification.Secret` | string? | `null` | Shared secret used for signature/header validation. Route files are secret-bearing config. |
| `Verification.SignatureHeaderName` | string? | `null` | Header name containing the HMAC signature. Defaults to `X-Webhook-Signature`. |
| `Verification.SignaturePrefix` | string? | `null` | Optional HMAC prefix such as `sha256=`. Defaults to empty string. |
| `Verification.SecretHeaderName` | string? | `null` | Header name for `HeaderSecret` mode. Defaults to `X-Webhook-Secret`. |
| `Verification.EventHeaderName` | string? | `null` | Event-name header. Defaults to `X-Webhook-Event`. |
| `Verification.DeliveryIdHeaderName` | string? | `null` | Delivery ID header. Defaults to `X-Webhook-Delivery`. |
| `Events` | string[] | `[]` | Optional allow-list of event types. Empty means all verified events are accepted. |
| `Audience` | string | `Public` | Source audience for the autonomous webhook session (`Public`, `Team`, `Personal`). |
| `Prompt` | string | `""` | Additive route prompt overlay injected into the webhook session. |
| `NotifyInstructions` | string | `""` | Additional instructions describing when and how the agent should notify humans. |
| `DeliveryRequired` | bool | `true` | Reminder-style delivery policy: when `true`, routes with notification instructions/targets fail if no notification is produced. |
| `NotificationTarget.Kind` | string | `Slack` | Human-facing notification channel type. Slack is the only implementation today. |
| `NotificationTarget.ChannelId` | string? | `null` | Slack channel ID used when the agent decides to notify. |
| `MaxBodyBytes` | int | `1048576` | Maximum accepted request-body size in bytes. Requests larger than this are rejected before dispatch. |
| `RateLimitPerMinute` | int | `30` | Maximum accepted deliveries per minute for this route. |

Route files are hot-reloaded on request. If a route file becomes missing,
malformed, or invalid, Netclaw removes that route immediately and returns `404`
for subsequent requests until the file is fixed.

Because route files may contain inline verification secrets, treat
`~/.netclaw/config/webhooks/` like `secrets.json`: restrict filesystem access
to operators, and use the dedicated webhook tools (`set_webhook`,
`list_webhooks`, `delete_webhook`) instead of broad generic file access when an
agent needs to manage routes.

### Telemetry

Optional OpenTelemetry export for logs and metrics.

```json
{
  "Telemetry": {
    "Enabled": true,
    "Otlp": {
      "Endpoint": "http://127.0.0.1:4317"
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Enabled` | bool | `false` | Enables OTLP export pipeline in daemon. |
| `Otlp:Endpoint` | string | `http://127.0.0.1:4317` | OTLP collector endpoint (gRPC). |

## Secrets

API keys and tokens should be stored in `secrets.json` using the same key
paths as `netclaw.json`. The configuration system merges them automatically.

```json
{
  "Slack": {
    "BotToken": "xoxb-your-bot-token",
    "AppToken": "xapp-your-app-token"
  },
  "Providers": {
    "openrouter": {
      "ApiKey": "sk-or-v1-your-key-here"
    }
  }
}
```

Recommended: `chmod 600 ~/.netclaw/config/secrets.json`

## Environment Variable Overrides

Environment variables with the `NETCLAW_` prefix override all file-based
configuration. Use `__` (double underscore) as the section separator,
following the standard .NET convention.

```bash
# Override the main model
export NETCLAW_Models__Main__Provider="openrouter"
export NETCLAW_Models__Main__ModelId="anthropic/claude-sonnet-4"

# Set a provider API key
export NETCLAW_Providers__openrouter__ApiKey="sk-or-v1-..."

# Set Slack tokens
export NETCLAW_Slack__BotToken="xoxb-..."
export NETCLAW_Slack__AppToken="xapp-..."

# Enable OTLP telemetry
export NETCLAW_Telemetry__Enabled="true"
export NETCLAW_Telemetry__Otlp__Endpoint="http://127.0.0.1:4317"

# Override session settings
export NETCLAW_Session__MaxToolCallsPerTurn="30"
```

## Complete Example

**netclaw.json:**

```json
{
  "Providers": {
    "local": {
      "Type": "ollama",
      "Endpoint": "http://localhost:11434"
    },
    "openrouter": {
      "Type": "openrouter",
      "Endpoint": "https://openrouter.ai/api/v1"
    }
  },
  "Models": {
    "Main": {
      "Provider": "local",
      "ModelId": "qwen3:30b",
      "ContextWindow": 32768
    },
    "Compaction": {
      "Provider": "local",
      "ModelId": "qwen3:8b"
    }
  },
  "Session": {
    "CompactionThreshold": 0.75,
    "SnapshotInterval": 20,
    "KeepRecentToolResults": 3,
    "MaxToolCallsPerTurn": 30,
    "TurnLlmTimeoutSeconds": 180,
    "ToolExecutionTimeoutSeconds": 90
  },
  "Tools": {
    "ShellTimeoutSeconds": 60,
    "MaxOutputChars": 32000
  }
}
```

**secrets.json:**

```json
{
  "Providers": {
    "openrouter": {
      "ApiKey": "sk-or-v1-your-key-here"
    }
  }
}
```

## Default Behavior

When no configuration files exist, Netclaw uses these defaults:

- **Provider**: Single `local-ollama` provider at `http://localhost:11434`
- **Main model**: `qwen3:30b` with 32,768 token context window
- **Fallback/Compaction**: Not configured (uses Main)
- **System prompt**: Seeded to `~/.netclaw/soul/PERSONALITY.md` on first run
