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
| `Type` | string | `"ollama"` | Provider SDK to use. Supported: `ollama`, `openai-compatible`, `openrouter`, `openai`, `anthropic`, `github-copilot`, `veniceai`. |
| `Endpoint` | string | `"http://localhost:11434"` | Base URL for the provider API. |
| `ApiKey` | string? | `null` | API key. Should go in `secrets.json` or an environment variable. |
| `OAuthTokenExpiry` | string? | `null` | Written by Netclaw to `secrets.json`, beside the OAuth tokens. It is the one `secrets.json` value that Netclaw does not encrypt, because the configuration binder cannot read an encrypted date. A key in `netclaw.json` is retired. See [OAuth token expiry](#oauth-token-expiry). |
| `VendorOptions` | object? | `null` | Provider-owned non-secret options. For `github-copilot`, `GitHubHost` and `GitHubApiBase` select the GitHub Enterprise host used for OAuth and Copilot token exchange; the Copilot API base remains `Endpoint`. |

### OAuth token expiry

Netclaw keeps the OAuth access token, the refresh token, the ChatGPT account ID, and the token
expiry of a provider in `secrets.json`. One refresh writes all of them in one save. A refresh does
not write `netclaw.json`, so it does not restart the daemon.

```json
{ "Providers": { "my-codex": { "OAuthAccessToken": "ENC:...", "OAuthTokenExpiry": "2026-06-01T01:00:00.0000000+00:00" } } }
```

- `secrets.json` encrypts every string value except `Providers.<name>.OAuthTokenExpiry`. The path
  has exactly three segments, and the names match without regard to case. The daemon reads the
  expiry as a date through the configuration binder, and the binder cannot read ciphertext. The
  expiry is refresh timing and not a credential. A value in another place is encrypted, even with
  the same key name.
- A refresh without an expiry writes a JSON `null`. The `null` hides a retired `netclaw.json` key.
- `netclaw secrets set Providers.<name>.OAuthTokenExpiry <date>` stores the date unencrypted. It
  rejects a value that is not an ISO 8601 date and changes nothing.
- A value that is not a date stops the daemon at startup with a message that names the provider.
  `netclaw provider` and the refresh service reject it too.
- An encrypted `OAuthTokenExpiry` in `secrets.json` comes from an older build. The daemon stops at
  startup with a message that names `Providers:<name>`. Sign in again with `netclaw provider`. The
  sign-in saves the date unencrypted.
- Caution: before you run an older release, delete each `Providers.<name>.OAuthTokenExpiry` from
  `secrets.json`. That value is plaintext, and an older daemon cannot read it. An older build
  encrypts the leaf again on its next save or `secrets set`, and its binder then fails on the
  encrypted date.

The refresh service reads `secrets.json` before it spends a refresh token. A refresh that the CLI
does (`netclaw provider` probes each provider) reaches a running daemon through that file.

- The daemon takes the stored refresh token only when it differs from the last token that the
  daemon read from or wrote to `secrets.json`. After a failed save, `secrets.json` holds a spent
  token, so the daemon keeps the live token in memory and never presents the spent one.
- The daemon remembers each refresh token that it exchanged for another one. When an older copy of
  `secrets.json` brings such a token back, the daemon keeps its in-memory tokens and logs a warning.
- A server that sends no new refresh token keeps the old one valid. The daemon writes the live
  refresh token again, so the file never keeps a spent one.
- When a save fails after a good refresh, the daemon logs an Error that names `secrets.json`. The
  file can hold a spent refresh token until the next good save.

The CLI and the daemon share no lock. A refresh at the same moment can present a token that the
other process just spent.

Older releases wrote `Providers.<name>.OAuthTokenExpiry` to `netclaw.json`. That key is retired.

- The daemon still binds the key. A value in `secrets.json`, a JSON `null` included, takes
  precedence. After the first token refresh the `secrets.json` value wins.
- The daemon logs a startup warning for each retired key that holds a value.
- `netclaw doctor` reports each retired key as information. The report does not change the exit
  code.
- `netclaw doctor --fix` copies `netclaw.json` to `netclaw.json.retired-oauth-expiry.bak`, or to the
  next free `netclaw.json.retired-oauth-expiry.N.bak`. Then it copies each date to an existing
  `secrets.json` provider entry that has no expiry, and it removes the key from `netclaw.json`. It
  does not create a provider entry, and it keeps a key whose value is not a date. It writes
  `secrets.json` first, so a crash between the two writes leaves both values, and the `secrets.json`
  value wins. It makes no `secrets.json` backup, because the backup of `netclaw.json` holds the date.
- When `netclaw.json` has comments, `doctor --fix` changes nothing, because the CLI would drop the
  comments. The report and the startup warning stay. Remove the key by hand. The daemon works
  meanwhile, because the `secrets.json` value wins after the first refresh.

### Models

Named model definitions own provider/model identity and metadata. Roles reference definitions,
so changing Main or Fallback does not destroy overrides belonging to the previous model.
Capability discovery does not write `ContextWindow`, `InputModalities`, or `OutputModalities`.
The daemon resolves those dynamic values at startup when the operator leaves them absent.

Older releases can contain discovery snapshots in these override fields. The stored shape
does not record each field's source. Netclaw preserves these values to protect explicit
operator overrides. Use `--clear-context-window` and `--clear-modalities` once to restore
runtime detection for an affected definition.

```json
{
  "Models": {
    "Definitions": {
      "qwen-main": {
      "Provider": "remote-gpu",
      "ModelId": "qwen3:30b",
      "ContextWindow": 32768
      },
      "qwen-small": {
        "Provider": "remote-gpu",
        "ModelId": "qwen3:8b",
        "ContextWindow": 32768
      }
    },
    "Roles": {
      "Main": "qwen-main",
      "Fallback": "qwen-small",
      "Compaction": "qwen-small"
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
| `ContextWindow` | int? | `null` | Operator override for the runtime context window. When set, it clamps the detected provider value. If not set, Netclaw uses the provider-reported value when available, otherwise defaults to 32,768. Model selection does not persist a discovered value here. |
| `InputModalities` | string? | `null` | Manual override for input modalities. Comma-separated flags from `Text`, `Image`, `Audio`, `Video` — e.g. `"Text"` or `"Text, Image"`. When set, bypasses automated capability detection. |
| `OutputModalities` | string? | `null` | Manual override for output modalities. Same form as `InputModalities`. |
| `MaxOutputTokens` | int? | `null` | Output limit for every call of this model, unless the call site sets its own. Must be positive, and below `ContextWindow` when that is set. Thinking tokens count inside it. If not set, each call sends 32,000, capped to the known model maximum and clamped to the known context window minus the estimated prompt and tool schemas (margin: 5% of the window, at least 1,024; floor: 1,024). A self-hosted provider counts only a window from `ContextWindow` or from the endpoint's own probe, ignores a catalog model maximum, and sends no limit field when the window is unknown. A self-hosted server must use the `ollama` or `openai-compatible` type; a hosted type (`deepseek`, `openrouter`, `veniceai`) pointed at your own server is treated as hosted. Set with `netclaw model set ... --max-output-tokens <n>`. |
| `Thinking` | string? | `null` | Provider-neutral thinking level: `Off`, `Low`, `Medium`, `High`, or `Max` (the highest level that the provider offers). If not set, the model's default applies. Netclaw rejects a level that the provider dialect or the model does not offer. Set with `netclaw model set ... --thinking <level>`. |

An `openai-compatible` provider needs `Providers:<name>:VendorOptions:ThinkingDialect` before it
can send a thinking level: `ChatTemplateKwargs`, `ReasoningEffort`, `DeepSeek`, or `None`.
`netclaw model set` saves `ReasoningEffort` when it detects llama.cpp. For another server, edit
the key in `netclaw.json`. Without the key, only `MaxOutputTokens` works.

**Invalid Models configuration.** One check, `ModelConfigurationValidation.Check`, validates the
Models section. The daemon runs it at startup. The config watcher runs it before it restarts the
daemon. It rejects these cases:

- legacy keys (`Main`, `Fallback`, `Compaction`) mixed with `Definitions`/`Roles`, in the file or in
  `NETCLAW_Models__*` environment variables
- `Definitions` without `Roles`, or `Roles` without `Definitions`
- a role that names an unknown definition
- a definition with a value that cannot be read (for example `InputModalities: "banana"`)
- a role-bound `ContextWindow` below 4,096
- a role-bound `MaxOutputTokens` that is not positive, or that is not below its `ContextWindow`
- a role-bound `Thinking` level that the provider's thinking dialect cannot send, an invalid
  `VendorOptions:ThinkingDialect` value, or a level other than `Off` on an `ollama` provider with
  `VendorOptions:DisableThinking: true`. `netclaw doctor --fix` does not change these values.
- a Fallback or Compaction model whose provider is not configured
- a provider that a role uses and that cannot be built: an unknown `Type`, a missing credential for
  `openai` or `anthropic`, an `openai-compatible` provider without `Endpoint`, or a `Providers` entry
  with a value that cannot be read (`AuthMethod`, `VendorOptions`, `OAuthTokenExpiry`). The check builds
  each role's client once and discards it; this makes no network call.
- a `netclaw.json` or `secrets.json` that the configuration source cannot read: invalid JSON, or a
  key written twice (`Models` and `models`)

At startup the daemon stops with exit code 1. It prints one `error:` line to stderr and writes the
same text to `daemon.log`. It writes no stack trace and no crash log. The message names the keys and
says what to remove or run. A missing Main model is not an error: it selects the No-Op chat client.

When the watcher finds the file invalid (any of the cases above, or invalid JSON), it logs one
warning per distinct reason and file content and does not restart the daemon. The
daemon keeps its previous configuration and applies no change from `netclaw.json` until the file is
fixed. `netclaw status` shows `config on disk not applied: <reason>` and reports
`overall: degraded`. `netclaw doctor` reports the same message.

### Session

Tuning parameters for LLM session behavior.

```json
{
  "Session": {
    "CompactionThreshold": 0.75,
    "SnapshotInterval": 20,
    "KeepRecentToolResults": 3,
    "MaxToolIterationsPerTurn": 60,
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
| `MaxToolIterationsPerTurn` | int | `60` | Legacy compatibility field. The default remains `60`, and old values stay readable. The runtime does not enforce this limit; tool loops follow recurrence governance. |
| `SidecarLlmTimeoutSeconds` | int | `90` | Timeout for sidecar LLM calls (title generation and compaction observations). |
| `TurnLlmTimeoutSeconds` | int | `180` | Timeout for the primary per-turn LLM streaming call before forcing an error/recovery path. |
| `ToolExecutionTimeoutSeconds` | int | `90` | Per-tool-call inactivity budget. A tool must produce its first result or stream item within this time, and each later item resets the budget. |

### Tools

Configuration for first-party tool execution.

`netclaw init` writes only the posture: `Security.DeploymentPosture`,
`Security.ShellExecutionMode`, `Security.StrictDefaults`, and `Tools.ShellMode`. It does not
write `Tools.AudienceProfiles` or any other default list. A second `netclaw init` replaces the
`Tools` section, so it deletes stored profiles; it does not write the defaults again.

The daemon computes each value in `Tools` in this order (`PolicyConfiguration.Bind`, at
startup; the result is process-wide):

1. It resolves the posture from `Security` (`SecurityPolicyDefaults.Resolve`).
2. It starts from the posture defaults (`ToolAudienceProfileDefaults.CreateProfilesForPosture`).
   For the Personal posture, the defaults include
   `Personal.ApprovalPolicy.ToolOverrides.shell_execute = Approval`.
3. It binds the `Tools` section on top. An absent key keeps the posture default. A present key
   is the operator's choice.

Examples:

- Positive: a Personal-posture file with no `ApprovalPolicy` gives `shell_execute = Approval`
  for Personal. A file where `netclaw mcp` wrote only `McpServerDefaults` for Personal keeps
  that rule.
- Negative: a file that sets `"shell_execute": "Auto"` for Personal gets `Auto`. The posture
  default does not override an explicit value.

`netclaw doctor` binds `netclaw.json` the same way. It does not report an absent profile,
because an absent profile is the posture default. It validates unsafe profile combinations
such as unrestricted `public` or `team` settings.

Audience profiles are independent from `Daemon.ExposureMode`: audience controls
who can interact with the bot in chat channels, while exposure mode controls
how the daemon is reachable over the network.

Use `netclaw doctor` when you want to inspect the effective audience-profile
shape, confirm that strict-default fallback is active, or verify that
`SandboxOnly` shell mode is still blocked until a sandbox backend is configured.

The example below shows the default profiles written out in full. Do not copy it into
`netclaw.json`: a stored copy of a default list does not get the tools that later releases add.
Write only the keys that you change.

```json
{
  "Tools": {
    "ShellMode": "HostAllowed",
    "MaxOutputChars": 32000,
    "AudienceProfiles": {
      "Public": {
        "ToolsMode": "Allowlist",
        "AllowedTools": [
          "file_read", "file_list", "file_search", "tool_output_read",
          "attach_file"
        ],
        "McpServersMode": "Allowlist",
        "AllowedMcpServers": [],
        "ReadFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
        "WriteFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] },
        "AttachFiles": { "Mode": "Roots", "Roots": ["{session_dir}"] }
      },
      "Team": {
        "ToolsMode": "Allowlist",
        "AllowedTools": [
          "file_read", "file_list", "file_search", "tool_output_read",
          "file_write", "file_edit", "attach_file", "web_search", "web_fetch", "skill_manage", "set_reminder",
          "list_reminders", "cancel_reminder", "get_reminder_history", "run_reminder",
          "set_working_directory"
        ],
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
| `MaxOutputChars` | int | `32000` | Maximum characters captured from tool output. |
| `AudienceProfiles` | object | built-in defaults | Per-audience tool, MCP server, and filesystem permissions. Default tool grants are monotonic — `public` ⊆ `team` ⊆ `personal`. `public` gets read-only file tools only (`file_read`, `file_list`, `file_search`, `tool_output_read`, `attach_file`) — no file mutation and no outbound web tools; `team` adds file mutation, web (`web_search`/`web_fetch`), scheduling, skill, and working-directory tools but not `shell_execute`, webhook tools, or any MCP server; `personal` defaults to unrestricted interactive tool/file access and all MCP servers. `AllowedTools` restricts only profile-managed tools. `public` and `team` file operations remain bounded by configured trusted roots and by their own session storage envelope. Only `personal` gets the shared Netclaw sessions root. See [tool authorization](../architecture/tool-authorization.md). |

A list in `Tools` that has default items replaces its default list. It does not add to it.
`ToolConfig.BindFromConfiguration` applies these rules to each such list (the
`ToolConfig.DefaultedLists` table): `AllowedTools` for Public and Team,
`ReadFiles`/`WriteFiles`/`AttachFiles` `Roots` for Public and Team,
`ChannelAttachments.AllowedCategories` for all three audiences, `GlobalReadRoots`, and
`WebFetch.HttpAllowList`.

- An absent key keeps the default list.
- Configured items replace the default list. For example,
  `"Team": { "AllowedTools": ["file_read", "file_list"] }` grants Team only those two tools.
  To add one entry, write the complete list, for example
  `"GlobalReadRoots": ["{skills_dir}", "{identity_dir}", "{workspaces_dir}", "/srv/docs"]`.
- `[]` or an empty `NETCLAW_*` variable gives an empty list.
- JSON `null` or `{}` gives an empty list, and the daemon logs a startup warning that names
  the key. `netclaw doctor` reports the same warning. These lists are all allow lists, so an empty list grants less.
- These shapes stop daemon startup with an error that names the key, and they do not print
  the value: a scalar value; an empty `NETCLAW_*` variable when `netclaw.json` or
  `secrets.json` sets items for the same key; an attachment category that is not one defined
  name (for example `"Bogus"`, `"3"`, or `"Pdf, Document"`). Category names match without case.

`netclaw doctor` warns, with no auto-fix, when a Public or Team allowlist does not include
`tool_output_read`. A large tool result spills to a file, and the notice tells the model to
call that tool.

Older installs: `netclaw init` 0.8.0 to 0.25.4, and 0.26.0-beta.1 to beta.5, wrote the
complete Public and Team default `AllowedTools` lists. Later releases changed those defaults;
for example, 0.26.0 added `file_search` and `tool_output_read`. The old binder added the current
defaults to the stored list, so those installs ran with the current defaults. The daemon keeps
that result with these rules:

- A Public or Team `AllowedTools` list in `Allowlist` mode that exactly matches an older
  shipped default (same tools in any order, no extra, missing, or repeated tool, same case) maps
  to the current default. The daemon logs a startup warning that names the audience, the tool
  changes, and the fix. A 0.8.0 to 0.19.0 Public list maps to the current Public default, which
  does not have `file_write`.
- A list that differs in any way is applied as written, and the daemon never widens it. This
  includes an edited older list, for example with Web Access turned off in the TUI, and the
  17-tool list that the 0.24 TUI wrote when it changed a profile from `All` to `Allowlist`. Such
  a list does not get `file_search` or `tool_output_read`.
- A list that exactly matches the current default is applied as written, with no warning.
- `netclaw doctor` reports each exact shipped list, which includes a copy of the current
  default. A stored copy does not get the tools that later releases add to the default.
- `netclaw doctor --fix` copies `netclaw.json` to `netclaw.json.legacy-tool-defaults.bak`, or to
  the next free `netclaw.json.legacy-tool-defaults.N.bak`, then deletes each Public or Team
  `AllowedTools` key in `Allowlist` mode that exactly matches a shipped list. The rest of the
  profile stays. The audience then follows the default, so the daemon applies the same tools
  before and after the fix. The fix never writes a default list. It never overwrites a backup,
  and a failed copy stops the write. A list that differs by one tool, and `[]`, stay.
- `ToolAudienceProfileToolCatalog.LegacyPublicDefaultAllowedTools` and
  `LegacyTeamDefaultAllowedTools` hold the shipped lists as policy data. The last row of each
  table is the current default, which `netclaw init` wrote from 0.26.0 to 0.27.1-beta.1. A test
  fails when the current default is not equal to the last row. The tables are closed: from the
  next 0.27.1 build, `netclaw init` writes no lists.

The daemon reads `netclaw.json`, then `secrets.json`, then `NETCLAW_*` variables. A later
source wins for each key. `netclaw doctor` reads only `netclaw.json`.

A later source does not replace a whole list. `IConfiguration` merges list items by index. For
example, `secrets.json` with `"AllowedTools": ["file_list"]` over `netclaw.json` with
`"AllowedTools": ["file_read", "attach_file"]` gives `["file_list", "attach_file"]`. The
`NETCLAW_*` form sets one index, for example `NETCLAW_Tools__WebFetch__HttpAllowList__0`. To
change a list, set it in one source only.

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
| `GrantCategory` | string? | `null` | Tool metadata category. Defaults to `mcp:{serverName}` when omitted. Authorization does not read it; use audience `AllowedMcpServers` and `McpServerToolGrants`. |
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
| `MentionRequiredInThreadByChannel` | object | `{}` | Per-channel map (channel ID → bool). When `true` for a channel, thread replies in that channel require a bot mention even when the thread already has an active session; a later mention re-reads the messages held since the last reply. A channel with no entry defaults to `false` (every reply in an active thread is routed without a mention). |
| `AllowedChannelIds` | string[] | `[]` | Allow-list of Slack channel IDs. Empty means no channels are allowed. |
| `AllowedUserIds` | string[] | `[]` | Optional allow-list of Slack user IDs. Empty means all users in allowed channels/DM policy are accepted. |

### Logging

Unified daemon logging settings used by both Microsoft.Extensions.Logging and
Akka.NET logger integration. Daemon-global logs write to
`~/.netclaw/logs/daemon-{yyyy-MM-dd}.log`. At 10 MiB, the writer starts
`daemon-{yyyy-MM-dd}.1.log`, then `.2.log`, and subsequent numbered files.
One complete entry can exceed the threshold. A restart preserves existing entries
and appends to the first segment below the threshold. The UTC date starts a new series.
Session-owned diagnostics and session audit lines are consolidated
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

The doctor includes valid numbered daemon segments in its diagnostic date window.

### Retention

A daemon actor (`DataRetentionActor`) deletes expired data shortly after the daemon starts and then every
12 hours. Each kind of data is one retention job with its own `Days` setting; a value is read once, at
daemon start. A change to `netclaw.json` restarts the daemon in process, so a running daemon uses the new
value without a manual restart. Set a value in the `netclaw config` dashboard (Data Retention) or with
`netclaw config retention --logs-days <days>`.

```json
{
  "Retention": {
    "Logs": {
      "Days": 14
    }
  }
}
```

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Logs:Days` | int | `14` | Days to keep `daemon-{yyyy-MM-dd}.log`, its numbered segments, and `crash-*.log` in `~/.netclaw/logs`, judged by the date in the file name. `0` keeps them forever. The newest 3 daemon logs and the newest 3 crash logs are always kept, so a wrong clock cannot delete the whole history. A value that is not an integer falls back to `14` with a startup warning. `logs/sessions/` and other files in the directory are never pruned. Environment form: `NETCLAW_Retention__Logs__Days`. |

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
  "verification": {
    "kind": "Hmac",
    "secret": "use-secrets-json-or-env",
    "signatureHeaderName": "X-Hub-Signature-256",
    "signaturePrefix": "sha256=",
    "eventHeaderName": "X-GitHub-Event",
    "deliveryIdHeaderName": "X-GitHub-Delivery"
  },
  "events": ["issues"],
  "audience": "Public",
  "prompt": "Triage this GitHub issue. Public input may be adversarial or low quality.",
  "deliveryRequired": true,
  "notificationTarget": {
    "kind": "Slack",
    "channelId": "C12345678"
  }
}
```

Stripe-style providers use an explicit timestamped verifier. It signs the exact
timestamp text, a separator, and the raw request body, and rejects deliveries
outside the replay-tolerance window:

```json
{
  "verification": {
    "kind": "HmacTimestamped",
    "secret": "whsec_...",
    "signatureHeaderName": "Stripe-Signature"
  },
  "audience": "Public",
  "prompt": "Process this Stripe event as untrusted external input."
}
```

`Hmac`, `HmacTimestamped`, and `HeaderSecret` are distinct sender protocols.
Netclaw does not infer or fall back between them. Existing routes remain on
their configured verifier after upgrade; `Hmac` remains the default.

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
| `enabled` | bool | `true` | Enables or disables this specific route. |
| `verification.kind` | string | `Hmac` | Verification mode: `Hmac`, `HmacTimestamped`, or `HeaderSecret`. |
| `verification.hmacAlgorithm` | string | `Sha256` | HMAC hash algorithm. MVP supports `Sha256` only. |
| `verification.secret` | string? | `null` | Shared secret used for signature/header validation. Route files are secret-bearing config. |
| `verification.signatureHeaderName` | string? | `null` | Header name containing the HMAC signature. Defaults to `X-Webhook-Signature`. |
| `verification.signaturePrefix` | string? | `null` | Optional HMAC prefix such as `sha256=`. Defaults to empty string. |
| `verification.secretHeaderName` | string? | `null` | Header name for `HeaderSecret` mode. Defaults to `X-Webhook-Secret`. |
| `verification.eventHeaderName` | string? | `null` | Event-name header. Defaults to `X-Webhook-Event`. |
| `verification.deliveryIdHeaderName` | string? | `null` | Delivery ID header. Defaults to `X-Webhook-Delivery`. |
| `verification.toleranceSeconds` | int? | `300` | Maximum past or future clock difference for `HmacTimestamped`, from 1 through 3600 seconds. |
| `verification.timestampField` | string? | `t` | Structured-header timestamp field for `HmacTimestamped`; must be an ASCII HTTP token and differ from the signature field. |
| `verification.signatureField` | string? | `v1` | Structured-header signature field for `HmacTimestamped`; follows the same HTTP-token constraint, and multiple instances support sender secret rotation. |
| `verification.signedPayloadSeparator` | string? | `.` | Separator between the exact timestamp text and raw body for `HmacTimestamped`. |
| `events` | string[] | `[]` | Optional allow-list of event types. Empty means all verified events are accepted. |
| `audience` | string | `Public` | Source audience for the autonomous webhook session (`Public`, `Team`, `Personal`). |
| `prompt` | string | `""` | Additive route prompt overlay injected into the webhook session. |
| `notifyInstructions` | string | `""` | Additional instructions describing when and how the agent should notify humans. |
| `deliveryRequired` | bool | `true` | Reminder-style delivery policy: when `true`, routes with notification instructions/targets fail if no notification is produced. |
| `notificationTarget.kind` | string | `Slack` | Human-facing notification channel type. Slack is the only implementation today. |
| `notificationTarget.channelId` | string? | `null` | Slack channel ID used when the agent decides to notify. |
| `maxBodyBytes` | int | `1048576` | Maximum accepted request-body size in bytes. Requests larger than this are rejected before dispatch. |
| `rateLimitPerMinute` | int | `30` | Maximum accepted deliveries per minute for this route. |

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

Service identity (`service.name`, `service.namespace`, `service.instance.id`) is
**not** configured in `netclaw.json`. It is read from the standard OpenTelemetry
environment variables — `OTEL_SERVICE_NAME` and `OTEL_RESOURCE_ATTRIBUTES` — the
same way every other OpenTelemetry service is configured. When those are unset,
netclaw applies sensible defaults: `service.name` = `netclawd`,
`service.instance.id` = `{hostname}:{processId}`, and `service.version` from the
running build. The resolved identity is logged at startup, and is stamped onto
operational webhook alerts so an alert and the telemetry from the same instance
can be correlated. Set `OTEL_SERVICE_NAME` to tell multiple netclaw instances
apart.

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
export NETCLAW_Models__Definitions__claude__Provider="openrouter"
export NETCLAW_Models__Definitions__claude__ModelId="anthropic/claude-sonnet-4"
export NETCLAW_Models__Roles__Main="claude"

# Set a provider API key
export NETCLAW_Providers__openrouter__ApiKey="sk-or-v1-..."

# Set Slack tokens
export NETCLAW_Slack__BotToken="xoxb-..."
export NETCLAW_Slack__AppToken="xapp-..."

# Enable OTLP telemetry
export NETCLAW_Telemetry__Enabled="true"
export NETCLAW_Telemetry__Otlp__Endpoint="http://127.0.0.1:4317"

# Override session settings
export NETCLAW_Session__MaxToolIterationsPerTurn="60"
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
    },
    "ds4": {
      "Type": "openai-compatible",
      "Endpoint": "http://127.0.0.1:8000"
    }
  },
  "Models": {
    "Definitions": {
      "qwen-main": {
        "Provider": "local",
        "ModelId": "qwen3:30b",
        "ContextWindow": 32768
      },
      "qwen-small": {
        "Provider": "local",
        "ModelId": "qwen3:8b"
      }
    },
    "Roles": {
      "Main": "qwen-main",
      "Compaction": "qwen-small"
    }
  },
  "Session": {
    "CompactionThreshold": 0.75,
    "SnapshotInterval": 20,
    "KeepRecentToolResults": 3,
    "MaxToolIterationsPerTurn": 60,
    "TurnLlmTimeoutSeconds": 180,
    "ToolExecutionTimeoutSeconds": 90
  },
  "Tools": {
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
