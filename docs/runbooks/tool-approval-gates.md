# Tool Approval Gates

Netclaw includes a tool approval system that requires interactive user sign-off
before executing potentially destructive tool calls. This guide covers how
approval gates work, how to configure them, and what to expect from each
channel.

## Overview

Tool invocations pass through three layers:

1. **Hard deny** — commands that are always blocked (e.g., `netclaw daemon stop`,
   `rm -rf /`). Never approvable. Checked first.
2. **Tool access** — per-audience allowlists (`AllowedTools`,
   `AllowedMcpServers`). Binary: the tool is available or it isn't.
3. **Approval gate** — for tools that pass layers 1 and 2, does this specific
   invocation need user sign-off?

The approval gate is transparent to the LLM — it never knows approval is
happening. It calls `shell_execute`, gets either a result or a denial.

## Approval Modes

Each tool can be in one of three modes per audience:

| Mode | Behavior |
|------|----------|
| `Auto` | No approval needed. Tool executes immediately. This is the default. |
| `Approval` | User must approve before execution. Unapproved commands pause and prompt. |
| `Deny` | Always blocked. No approval prompt offered. |

## Configuration

Approval is configured per audience via `ApprovalPolicy` on each audience
profile in `netclaw.json`:

```json
{
  "Tools": {
    "AudienceProfiles": {
      "Personal": {
        "ApprovalPolicy": {
          "DefaultMode": "Auto",
          "ToolOverrides": {
            "shell_execute": "Approval"
          }
        }
      }
    }
  }
}
```

This means: all tools run normally, but `shell_execute` requires approval for
the Personal audience. You can add other tools to `ToolOverrides` as needed
(e.g., `"mcp:filesystem:write_file": "Approval"`).

### New installations

`netclaw init` sets `shell_execute: Approval` for Personal audience by default.
The operator can change this in the generated config.

### Existing installations

Existing configs without `ApprovalPolicy` are unaffected — all tools remain in
`Auto` mode. Add the `ApprovalPolicy` section manually or rerun `netclaw init`.

### Headless mode

Headless mode (`netclaw -p "prompt"`) cannot ask for approval — there is no
interactive user. Approval-gated tools are **automatically denied** in headless
mode. If you need unrestricted shell in headless scripts, explicitly set
`shell_execute` to `Auto`:

```json
{
  "Tools": {
    "AudienceProfiles": {
      "Personal": {
        "ApprovalPolicy": {
          "ToolOverrides": {
            "shell_execute": "Auto"
          }
        }
      }
    }
  }
}
```

## How Approval Works

When the agent calls a tool in `Approval` mode:

1. The system extracts a **command pattern** (e.g., `git push` from
   `git push origin main`).
2. It checks the **approval cache** — has this pattern been approved before?
3. If not approved, the channel posts an approval prompt:
   ```
   🔒 Tool approval required
   > shell_execute: git push origin main
   Pattern: git push

   Reply with:
     A) Approve once
     B) Approve always
     C) Deny
   ```
4. The tool execution pauses until the user responds. Other tool calls in the
   same batch continue running independently.
5. Based on the response:
   - **Approve once** — command executes; approval valid for this session only
   - **Approve always** — command executes; pattern persisted to disk
   - **Deny** — command returns "Command denied by user" to the LLM

### Command patterns

For `shell_execute`, patterns are verb-chain prefixes extracted by tokenizing
the command:

| Command | Pattern |
|---------|---------|
| `git push origin main` | `git push` |
| `docker compose up -d` | `docker compose` |
| `ls -la /tmp` | `ls` |
| `dotnet build --configuration Release` | `dotnet build` |

Approving `git push` covers all `git push` variants. A typical workflow
produces ~10 patterns after a week of use.

For **compound commands** (`&&`, `||`, `;`, `|`), each segment is checked
independently. If any segment is unapproved, all unapproved patterns are
batched into one prompt.

For **non-shell tools** (MCP tools, `file_write`, etc.), approval is at the
tool-name level — either the tool is approved or it isn't.

### Persistent approvals

"Approve always" decisions are stored in
`~/.netclaw/config/tool-approvals.json`:

```json
{
  "audiences": {
    "personal": {
      "shell_execute": ["git push", "git add", "dotnet build", "dotnet test"]
    }
  }
}
```

This file is **not** monitored by the config watcher — writing to it does not
restart the daemon. Each audience has its own section.

## Hard Deny List

Some commands are categorically blocked and cannot be approved, regardless of
mode:

| Category | Examples |
|----------|---------|
| Self-destructive | `netclaw daemon stop`, `kill`, `killall`, `pkill`, `systemctl stop netclaw` |
| System-destructive | `rm -rf /`, `rm -rf ~/`, fork bombs, `mkfs` |

The hard deny check runs even in `Auto` mode (no approval configured).

### Custom hard deny patterns

Add patterns via `HardDenyPatterns` in `netclaw.json`:

```json
{
  "Tools": {
    "HardDenyPatterns": ["docker rm", "kubectl delete namespace"]
  }
}
```

Custom patterns are added to the defaults — they don't replace them.

## Channel Support

| Channel | Supports approval? | Rendering |
|---------|-------------------|-----------|
| Slack | Yes | Text prompt with ABC options (Block Kit buttons planned) |
| TUI (`netclaw chat`) | Yes | Inline prompt |
| SignalR (web client) | Yes | Inline prompt |
| Headless (`netclaw -p`) | No — auto-deny | N/A |
| Reminders | No — auto-deny | N/A |
| Webhooks | No — auto-deny | N/A |

If a channel doesn't support approval, the tool is immediately denied with
reason `channel_does_not_support_approval`.

## Diagnostics

`netclaw doctor` checks approval configuration for common issues:

- **Approval mode enabled but shell off**: warns when `shell_execute` is in
  `Approval` mode but `ShellMode` is `Off` (config has no effect)
- **Stale persistent approvals**: warns when `tool-approvals.json` has patterns
  for an audience where shell is disabled

## Audit

Tool audit entries include `ApprovalDecision` and `ApprovalPattern` fields when
a tool goes through the approval flow:

```
Tool executed: shell_execute (approved, pattern=git push)
Tool denied: shell_execute (denied_by_user, pattern=docker rm)
Tool denied: shell_execute (timed_out, pattern=kubectl apply)
```

## FAQ

**Q: Can I disable approval gates entirely?**
A: Yes. Either remove the `ApprovalPolicy` section from your audience profile,
or set all tools to `Auto` mode.

**Q: Can I require approval for MCP tools?**
A: Yes. Add the tool name to `ToolOverrides`:
```json
"ToolOverrides": {
  "shell_execute": "Approval",
  "mcp:memorizer:store": "Approval"
}
```

**Q: What happens if I don't respond to an approval prompt?**
A: The prompt times out after 5 minutes and auto-denies. The LLM receives
"Approval timed out" as the tool result.

**Q: Can I pre-approve common commands?**
A: Yes. Edit `~/.netclaw/config/tool-approvals.json` directly to add patterns.
Or use the agent normally — every "Approve always" click adds to the file.
