# Tool Approval Gates

Netclaw includes a tool approval system that requires interactive user sign-off
before executing potentially destructive tool calls. This guide covers how
approval gates work, how to configure them, and what to expect from each
channel.

## Overview

Tool invocations pass through four layers:

1. **Operation hard deny** — shell commands that are always blocked
   (e.g., `netclaw daemon stop`, `rm -rf /`). Never approvable. Checked first.
2. **Resource hard deny** — protected files and directories (secrets, keys,
   lifecycle/control-plane files) that are blocked for file tools and shell
   path references. Never approvable.
3. **Tool access** — per-audience allowlists (`AllowedTools`,
   `AllowedMcpServers`). Binary: the tool is available or it isn't.
4. **Approval gate** — for tools that pass layers 1-3, does this specific
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

Headless mode (`netclaw chat -p "prompt"`) cannot ask for approval — there is no
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
| `git push origin main` | `git push origin main` |
| `docker compose up -d` | `docker compose up` |
| `ls -la /tmp` | `ls` |
| `dotnet build --configuration Release` | `dotnet build` |

Extraction is greedy: bare-word operands (subcommands, remote names, branch
names) stay in the verb chain; the chain stops at the first flag, path, or
URL. Approving `git push origin main` covers later `git push origin main`
calls, not `git push origin dev` — each distinct verb chain is its own
pattern.

For **compound commands** (`&&`, `||`, `;`, `|`), each segment is checked
independently. If any segment is unapproved, all unapproved patterns are
batched into one prompt.

For most **non-shell tools** (MCP tools, `file_read`, etc.), approval is at the
tool-name level.

For `file_write` and `file_edit`, approval is path-aware for Netclaw
control-plane targets. Writes under the control-plane root use mode keys like
`file_write:control-plane` / `file_edit:control-plane` and persist approvals as
path-scoped patterns (for example,
`file_write:control-plane:netclaw.json`).

### Read-only verbs skip the prompt

Demonstrably read-only verbs auto-run with no prompt when invoked inside a
trusted zone (`session_dir`, or `project_dir` for Personal/Team). The bundled
safe-verb lists (`safe-verbs.linux.json`, `safe-verbs.windows.json`) cover file
readers (`ls`, `grep`, `cat`), system/info verbs (`date`, `whoami`, `uname`,
`uptime`), and read-only `git`/`gh` queries (`git status`, `git log`,
`gh pr view`, `gh run list`). Mutating verbs (`git push`, `git fetch`, `rm`),
command-prefixing verbs (`env`, `xargs`, `sudo`), network-writing verbs
(`gh api`, `curl`), and environment/process-inspection verbs (`printenv`,
`ps`) are never auto-allowed — the trusted-zone gate scopes verbs that act on
a path, so it cannot contain a verb that dumps the process environment or the
process table. The list ships with the daemon and is widened only through
code review — the agent cannot extend its own auto-pass surface. A compound
command auto-runs only when every clause is a safe verb; a single mutating
clause makes the whole command prompt.

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

In addition to command hard deny, Netclaw enforces path hard deny for protected
resources (for example `secrets.json`, key material, webhook secrets, and
control-plane lifecycle files). Those accesses are blocked for file tools and
for shell commands that reference those paths.

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
| Headless (`netclaw chat -p`) | No — auto-deny | N/A |
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
a tool goes through the approval flow. Later calls that are satisfied by a
session or persistent approval are audited as `PreviouslyApproved`; the pattern
includes the matched source and scope so operators can tell which grant applied.
Near-miss diagnostics are also emitted to the daemon log when a same-verb
persistent shell grant exists but does not match the candidate directory/cwd.

```
Tool executed: shell_execute (approved, pattern=git push)
Tool executed: shell_execute (PreviouslyApproved, pattern=git push [persistent: git push in /home/user/repo])
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
A: While the daemon and session remain alive, approval waits remain pending until
you approve, deny, or the blocked run is cancelled. Parent-session approvals have
durable recovery state and can be redriven after cold recovery. Subagent approval
waits are live-only; if the daemon or parent session restarts before you respond,
the stale prompt is rejected as expired and the interrupted `spawn_agent` call is
closed before the next turn continues.

**Q: Can I pre-approve common commands?**
A: Yes. Edit `~/.netclaw/config/tool-approvals.json` directly to add patterns.
Or use the agent normally — every "Approve always" click adds to the file.
