# Configuration Reference

Source PRDs: PRD-001

## Overview

Netclaw uses a layered configuration system based on standard
`Microsoft.Extensions.Configuration`. Settings are loaded from three sources
in priority order (later sources override earlier ones at the same key path):

1. `~/.netclaw/config/netclaw.json` — base configuration (optional)
2. `~/.netclaw/config/secrets.json` — credential overlay (optional)
3. Environment variables with `NETCLAW_` prefix (highest priority)

With no configuration files present, Netclaw defaults to a local Ollama
instance at `http://localhost:11434` using `qwen3:30b`.

## Directory Structure

All configuration lives under `~/.netclaw/`:

```
~/.netclaw/
├── config/
│   ├── netclaw.json        # Base settings
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
      "Endpoint": "http://big-gpu:11434"
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
| `ContextWindow` | int? | `null` | Context window size in tokens. Defaults to 32,768 if not set. Future: auto-discovered from provider. |

### Session

Tuning parameters for LLM session behavior.

```json
{
  "Session": {
    "CompactionThreshold": 0.75,
    "SnapshotInterval": 20,
    "KeepRecentToolResults": 3,
    "MaxToolIterationsPerTurn": 10
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `CompactionThreshold` | double | `0.75` | Context usage ratio (0.0–1.0) at which compaction triggers. |
| `SnapshotInterval` | int | `20` | Number of turns between persistence snapshots. |
| `KeepRecentToolResults` | int | `3` | Recent tool call/result pairs kept in full during compaction. |
| `MaxToolIterationsPerTurn` | int | `10` | Max tool execution rounds per turn before forcing a text response. |

### Tools

Configuration for first-party tool execution.

```json
{
  "Tools": {
    "ShellTimeoutSeconds": 60,
    "MaxOutputChars": 32000
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `ShellTimeoutSeconds` | int | `60` | Timeout for shell command execution. |
| `MaxOutputChars` | int | `32000` | Maximum characters captured from tool output. |

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
| `AllowDirectMessages` | bool | `true` | If true, DM messages do not require mention. |
| `AllowedChannelIds` | string[]? | `null` | Optional allow-list of Slack channel IDs. |
| `AllowedUserIds` | string[]? | `null` | Optional allow-list of Slack user IDs. |

### Logging

Unified daemon logging settings used by both Microsoft.Extensions.Logging and
Akka.NET logger integration.

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
| `LogLevel:Default` | string | `Warning` | Minimum log level (`Debug`, `Information`, `Warning`, `Error`, etc.) shared by MEL and Akka.NET. |
| `Console:Enabled` | bool | `false` | Enables console logger provider output for daemon debugging. |

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

# Override session settings
export NETCLAW_Session__MaxToolIterationsPerTurn="5"
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
    "MaxToolIterationsPerTurn": 10
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
