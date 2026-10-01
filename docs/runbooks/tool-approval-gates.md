# Tool Approval Gates

This runbook tells an operator how to configure, answer, inspect, and repair
tool approval in Netclaw. It covers configuration, the CLI, channels, and
diagnostics.

For how authorization works inside Netclaw, read
[the tool authorization architecture](../architecture/tool-authorization.md).
For the testable rules, read the
[`tool-authorization` capability](../../openspec/specs/tool-authorization/spec.md).

## What happens to a tool call

Netclaw checks each tool call before it runs. The result is one of four
outcomes:

| Outcome | What you see |
| --- | --- |
| Allowed | The tool runs. |
| Requires approval | The channel shows a prompt. The call waits for your answer. |
| Requires correction | The agent gets advice and sends a different call. You see no prompt. |
| Denied | The tool does not run. No answer can change this. |

These checks run first and cannot be overridden by an approval:

- The audience profile must allow the tool.
- Shell calls need the Personal audience and a shell mode that allows the host
  shell.
- A hard-deny rule or a protected path denies the call.

## Approval modes

Each audience sets a mode per tool:

| Mode | Behavior |
| --- | --- |
| `Auto` | No prompt. Hard deny, path checks, and agent corrections still apply. |
| `Approval` | Netclaw asks you unless a grant or a safe rule covers the call. |
| `Deny` | The call is always blocked. No prompt. |

## Configuration

Set the mode in the `ApprovalPolicy` of each audience profile in
`netclaw.json`:

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

This means: all tools run without a prompt, but `shell_execute` needs approval
for the Personal audience. Add other tools to `ToolOverrides` as needed, for
example `"mcp:filesystem:write_file": "Approval"`. `McpServerDefaults` sets a
mode for every tool of one MCP server.

Defaults you get without an `ApprovalPolicy`:

- Personal `shell_execute` uses `Approval`, even when `DefaultMode` is `Auto`.
  Only an exact `shell_execute` override of `Auto` removes the prompt.
- Personal `file_write` and `file_edit` on a Netclaw control-plane path use
  `Approval`.
- Other tools use `Auto`.
- Team and Public cannot use `shell_execute`.

`netclaw init` writes an explicit `shell_execute: Approval` override for
Personal. Rerun `netclaw init` or add the override to make the default visible.

### Shell mode

`Tools.ShellMode` selects `Off`, `SandboxOnly`, or `HostAllowed`. When it is
absent, Netclaw uses `Security.ShellExecutionMode`, then the posture default
(`HostAllowed` for Personal). `SandboxOnly` always denies today, because
Netclaw has no sandbox backend.

### Headless and unattended runs

Headless chat (`netclaw chat -p "prompt"`), reminders, and webhooks cannot show
a prompt. Netclaw still applies hard deny, path checks, and stored grants. If a
call still needs a prompt, the tool result is:

```text
Tool requires approval but no interactive approval requester is available: <tool>
```

The reviewed-safe catalog does not cover a call in these runs. Output commands
such as `echo` still pass. To let a headless script run shell commands without
a prompt, set `shell_execute` to `Auto`:

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

## Answer a prompt

A prompt shows the tool, the command or arguments with secrets removed, and
the options that are safe for this call:

| Option | Key | Effect |
| --- | --- | --- |
| Once | `approve_once` | The exact blocked call runs once. Netclaw saves nothing. |
| This chat | `approve_session` | The phrase is allowed for the rest of this session. |
| Always here | `approve_always` | Netclaw saves a grant for this folder. |
| This repository | `approve_repository` | Netclaw saves a grant for one Git repository and its registered worktrees. |
| Always anywhere | `approve_everywhere` | Netclaw saves a global grant. For an MCP tool, the label is "Always allow this tool". |
| Deny | `deny` | This call does not run. Netclaw does not ban the phrase. |

The prompt offers fewer options when a broader grant is not safe:

- Only `Once` and `Deny` appear when the shell parser cannot prove a reusable
  phrase for every command in the call, or when the call is a managed
  temporary directory retry.
- Only `Once` and `Deny` appear when a shell path has a `..` segment that
  leaves a symbolic link. The OS follows the link before it applies `..`. If
  `lnk` points to `/data/deep`, then `cat lnk/../notes.txt` reads
  `/data/notes.txt` and not `./notes.txt`. No grant or reviewed-safe phrase
  covers such a call, and a headless call is denied. A `..` that leaves an
  ordinary directory keeps its normal approval behavior.
- `Always here` is absent for non-shell tools, for a shallow directory, and
  for a session-owned directory.
- `This repository` appears only for a shell call whose commands all resolve
  to one registered Git repository. Netclaw does not offer it for a main
  checkout that uses `--separate-git-dir`.

Only the person who started the request can answer it, unless the request came
from verified automation. Netclaw rejects an option that the prompt did not
offer.

A prompt waits until you answer. No timer denies it. If the daemon restarts or
the session goes idle, your later answer still resumes the call. A new message
in the thread abandons the calls that wait. A subagent prompt does not survive a
restart; Netclaw rejects the old prompt as expired.

## What runs without a prompt

A call in `Approval` mode runs without a prompt when every part of it is
covered:

- A grant that you saved (this chat, a folder, a repository, or everywhere)
  covers the phrase.
- The command is an output command: `echo`, `printf`, `:`, `true`, or `false`.
- In an interactive session, the reviewed diagnostic catalog covers the
  phrase, and every path is inside the session or project directory. The
  catalog ships with the daemon (`safe-verbs.linux.json`,
  `safe-verbs.windows.json`). It includes readers such as `ls`, `cat`, `grep`,
  `Get-Content`, and `Select-String`, and queries such as `git status` and
  `gh run list`. It never includes `git push`, `rm`, `env`, `xargs`, `sudo`,
  `curl`, `gh api`, or `printenv`. The agent cannot extend it.

For a compound command (`&&`, `||`, `;`, `|`), each command needs its own
coverage. The prompt asks only for the commands that remain uncovered.

A Bash `cd` with an exact target changes the directory of the commands after
it. Netclaw checks each command in each directory where it can run. For
`cd /tmp && gh api ... > log; wc -c log`, `wc` can run in `/tmp`, or in the
original directory when `cd` fails, so it needs coverage in both. The prompt
offers reusable grants, and an `Always here` grant uses that directory, not
the session directory. A reviewed diagnostic after `cd dir && action;` is
covered inside `dir` in an interactive session. A dynamic target (`cd "$X"`),
`cd -`, `pushd`, a `cd` in a subshell, function, or pipeline, and a linked
target directory still offer only `Once` and `Deny`. An unattended run denies
the `cd dir && action; diagnostic` shape as unresolved input.

## Manage saved grants

Netclaw stores saved grants in `~/.netclaw/config/tool-approvals.json`
(version 3). Each audience has its own section, and each tool has its own
list. A grant for `shell_execute` never allows another tool.

Use the CLI. Do not edit the file by hand.

```bash
netclaw approvals list
netclaw approvals list --json
netclaw approvals trust-verb "git push" --shell bash
netclaw approvals revoke 'Bash token-prefix "git push" anywhere'
netclaw approvals revoke --tool shell_execute --all --audience personal
```

- `trust-verb` accepts one complete static phrase. It rejects a flag, a
  redirect, an assignment, a dynamic command name, and a compound command.
- For a non-shell tool, use `--tool`. Do not use `--shell` with a non-shell
  tool.
- Use `netclaw approvals list` to copy the exact label of a repository grant
  before you revoke it.

The daemon does not watch this file. A change to it does not restart the
daemon.

### Upgrade, rollback, and repair

- On the first load of a version 2 file, Netclaw writes a byte-identical
  `tool-approvals.json.v2.bak` and converts the file. Old shell entries become
  exact-phrase (`LegacyExact`) grants, so an upgrade adds no authority.
- To recover the old file: stop the daemon, copy the backup over the active
  file, and start the current daemon. Do not run a version 2 daemon against a
  version 3 file.
- An older version 3 binary can reject an entry with `assignmentDigest`.
  Remove those entries, or restore the backup, before a rollback.
- Netclaw does not change a malformed, partial, or future-version file. It
  marks the store unavailable. A call that needs a grant is then denied with
  `approval_store_unavailable`. Fix or restore the file to recover.

## Hard deny

Some commands and paths are always blocked, in every mode:

| Category | Examples |
| --- | --- |
| Self-destructive | `netclaw daemon stop`, `kill`, `killall`, `pkill`, `systemctl stop netclaw` |
| System-destructive | `rm -rf /`, `rm -rf ~/`, fork bombs, `mkfs` |
| Privilege escalation | `sudo`, `su`, `doas`, and a PowerShell `-Verb RunAs` start |
| Protected paths | `secrets.json`, key material, webhook secrets, the grant store, the Netclaw database, and daemon lifecycle files |

File tools can read ordinary `netclaw.json`. A shell command that names the
Netclaw config directory is denied.

Add your own command patterns with `HardDenyPatterns`. They add to the
built-in list; they do not replace it:

```json
{
  "Tools": {
    "HardDenyPatterns": ["docker rm", "kubectl delete namespace"]
  }
}
```

## Channel support

| Channel | Shows a prompt? | Prompt form |
| --- | --- | --- |
| Slack | Yes | Block Kit buttons |
| Discord | Yes | Native buttons |
| Mattermost | Yes | Interactive buttons, or a text reply when no callback URL is set |
| TUI (`netclaw chat`) | Yes | Inline prompt |
| SignalR (web client) | Yes | Inline prompt |
| Headless (`netclaw chat -p`) | No | The call is denied with the headless result text |
| Reminders | No | Same as headless |
| Webhooks | No | Same as headless |

In a channel without prompts, a saved grant or an output command can still
cover a call. If a channel cannot post a prompt (for example, the platform
rejects the message), the channel answers `Deny` for that call.

## Diagnostics

### Doctor

`netclaw doctor` checks the approval configuration:

- `shell_execute` is in `Approval` mode but `ShellMode` is `Off`. The approval
  setting has no effect.
- Saved shell grants exist, but shell is disabled.
- Personal sets `shell_execute` to `Auto` while the host shell is enabled.

### Daemon log lines

Netclaw has no separate audit store. The daemon log records each decision:

```text
Tool authorization evaluated: {ToolName} outcome={AuthorizationOutcome} ... authorizationAttemptId={AuthorizationAttemptId} sessionId={SessionId} callId={CallId}
Tool executed: {ToolName} ({Duration}ms, {ResultLength} chars) authorizationAttemptId=... sessionId=... callId=...
```

- A `Denied` line is a warning and includes `reason=`, for example
  `hard_deny_self_destructive`, `tool_not_allowed_for_audience_profile`, or
  `shell_path_outside_trust_zone`.
- An `Allowed` line is at debug level.
- One `authorizationAttemptId` joins the decision, the prompt, your answer,
  and the retry of one call.

The session journal records each prompt (`ToolApprovalRequested`) and each
answer (`ToolApprovalResolved`).

### Shell policy trace

Each shell decision also writes ordered `Shell policy trace:` rows. A row holds
only enum facts, a call-local candidate ID, a short redacted executable name,
the coverage, the scope relation, and the grant time. It never holds the full
command, arguments, paths, or secrets.

```text
Shell policy trace: stage=StoredGrantMatch outcome=Covered reason=PersistentGlobalGrant candidate_id=0 executable=gh coverage=PersistentGlobal scope_relation=Global grant_timestamp=2026-08-13T00:00:00.0000000+00:00
Shell policy trace: stage=StoredGrantMatch outcome=Uncovered reason=NoGrant candidate_id=1 executable=head coverage=Uncovered scope_relation=None grant_timestamp=(null)
Shell policy trace: stage=Completion outcome=RequiresApproval reason=UncoveredCandidates candidate_id=(null) executable=(null) coverage=(null) scope_relation=None grant_timestamp=(null)
```

Read a trace from the last row back:

1. `Completion/RequiresApproval/UncoveredCandidates` means at least one
   command had no coverage.
2. Find that candidate ID in the `StoredGrantMatch` rows. `NoGrant` means no
   saved grant matched. `TokenMismatch`, `ShellMismatch`, `OutsideDirectory`,
   or `Symlink` explains a near miss.
3. A `ReviewedSafePolicy` or `OneTimeApproval` row for that ID means a later
   stage covered it.
4. `Completion/Deny/InternalPolicyFailure` is a defect, not an approval case.
   Do not add a grant to work around it. Report it.
5. `Trace/TraceTruncated/TraceLimitReached` means the row cap was reached. It
   does not change the result.

Use the trace before you ask for a policy change:

- A same-phrase `OutsideDirectory` near miss usually means that the working
  directory moved.
- A command that stays unresolved although its effect is clear is a parser
  gap. Report it for ShellSyntaxTree. Netclaw does not add parsers for one
  executable.

## FAQ

**Q: Can I turn off approval for shell commands?**
A: Yes. Set an exact `shell_execute` override to `Auto`. A removed override
does not turn off the Personal default.

**Q: Can I require approval for an MCP tool?**
A: Yes. Add the tool to `ToolOverrides`:

```json
"ToolOverrides": {
  "shell_execute": "Approval",
  "mcp:memorizer:store": "Approval"
}
```

**Q: What happens if I do not answer a prompt?**
A: The call waits. No timer denies it. Your answer still works after a daemon
restart, except for a subagent prompt.

**Q: Can I approve common commands in advance?**
A: Yes. Use `netclaw approvals trust-verb`, or choose a saved-grant option in a
prompt.

**Q: Why did Netclaw deny a command that I approved before?**
A: A grant covers one phrase for one audience and one tool, inside its scope.
A different phrase, a different folder or repository, or a protected path needs
new approval or stays denied. Read the shell policy trace.
