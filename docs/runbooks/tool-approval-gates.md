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

Personal `shell_execute` calls without an exact override use the fail-closed
`Approval` mode. This rule also applies when the Personal `ApprovalPolicy` is
absent or its `DefaultMode` is `Auto`.

Rerun `netclaw init` or add the `Approval` override to make this behavior
explicit. Set an exact `shell_execute` override to `Auto` only when shell
commands must run without approval.

### Headless mode

Headless mode (`netclaw chat -p "prompt"`) cannot ask for approval — there is no
interactive user. Netclaw still applies hard deny, path policy, trust zones, and
stored grants. If any candidate remains uncovered and would need a
prompt, the call is denied. The reviewed-safe catalog alone does not grant a
headless call; approval-exempt side effects can still pass. For unrestricted
shell in headless scripts, explicitly set `shell_execute` to `Auto`:

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

1. The system extracts a **command pattern** (for example,
   `git push origin main` from that exact call).
2. It checks the **approval cache** — has this pattern been approved before?
3. If a clean reusable shell call in an ordinary directory remains uncovered,
   the channel posts the default five-choice prompt:
   ```
   🔒 Tool approval required
   > shell_execute: git push origin main
   Pattern: git push origin main

   Reply with:
     A) Once
     B) This chat
     C) Always here
     D) Always anywhere
     E) Deny
   ```
4. The tool execution pauses until the user responds. Other tool calls in the
   same batch continue running independently.
5. Based on the response:
   - **Once** — the exact blocked call retries once; no grant is saved
   - **This chat** — the covered phrase remains valid anywhere in this session
   - **Always here** — a folder-scoped grant is saved to disk
   - **Always anywhere** — a global phrase grant is saved to disk
   - **Deny** — the call returns "Command denied by user" to the LLM

The policy can remove reusable choices when a command has no clean phrase. It
also removes `Always here` for a shallow root, session scratch, and non-shell
tools that have no directory scope. See [When the prompt offers fewer
buttons](#when-the-prompt-offers-fewer-buttons).

### When the prompt offers fewer buttons

`Once` and `Deny` are the fail-closed choices. Netclaw omits `This chat` and
the persistent choices when the shell parser cannot produce a clean reusable
phrase for every uncovered command occurrence. It also omits `Always here`
when no safe directory scope can be stored. This rule prevents a one-time
decision from becoming broader reusable authority.

### Command patterns

For `shell_execute`, patterns come from the parser for the daemon's selected
native shell environment. Linux and macOS use Bash. Windows uses a probed
native PowerShell host: compatible PowerShell 7.6 is preferred, with Windows
PowerShell 5.1 as the fallback. The exact executable, grammar, and dialect are
shown in Personal session working context.

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

The selected host grammar is also the language boundary. Under Bash,
`pwsh -Command 'Get-Content ./a.txt'` is an ordinary external `pwsh` command;
the payload is not separately parsed as PowerShell. Under native PowerShell,
`bash -c 'cat ./a.txt'` is likewise an ordinary external `bash` command.
Same-language static child hosts can expose nested command occurrences when
ShellSyntaxTree proves them.

PowerShell 7 and Windows PowerShell 5.1 are analyzed as distinct dialects.
In particular, `&&` and `||` are unresolved under 5.1 and cannot create a
persistent approval candidate or receive the reviewed diagnostic shortcut.
Incomplete commands, dynamic command identities, and non-filesystem provider
drives also remain one-time-only. Netclaw does not claim knowledge of ambient
profiles, modules, inherited variables, executable lookup, or external script
contents.

For most **non-shell tools** (MCP tools, `file_read`, etc.), approval is at the
tool-name level.

For `file_write` and `file_edit`, approval is path-aware for Netclaw
control-plane targets. Writes under the control-plane root use mode keys like
`file_write:control-plane` / `file_edit:control-plane` and persist approvals as
path-scoped patterns (for example,
`file_write:control-plane:netclaw.json`).

### Reviewed diagnostic phrases skip the prompt

In an interactive session, reviewed diagnostic phrases auto-run inside a
trusted zone. Personal and Team use `session_dir` or `project_dir`. In a
headless session, this catalog does not grant authority. The call still needs
an exact one-time, session, or persistent grant. The bundled safe-policy
catalogs (`safe-verbs.linux.json`, `safe-verbs.windows.json`) cover
file readers (for example `ls`, `grep`, and `cat` on Bash; `Get-ChildItem`,
`Get-Content`, and `Select-String` on PowerShell), system/info phrases
(`whoami`, `uname`, `uptime`), and narrowly reviewed `git`/`gh` queries
(`git status`, `git rev-parse`, `gh run list`). Mutating verbs (`git push`, `git fetch`, `rm`),
command-prefixing verbs (`env`, `xargs`, `sudo`), network-writing verbs
(`gh api`, `curl`), and environment/process-inspection verbs (`printenv`,
`ps`) are never auto-allowed — the trusted-zone gate scopes verbs that act on
a path, so it cannot contain a verb that dumps the process environment or the
process table. Each entry stores canonical shell tokens and a proof category.
`ReviewedDiagnostic` classifies the shell-authored invocation. It does not
claim that Netclaw sandboxes the executable.

The reviewed phrase cannot accept an authored helper command, output file,
destructive state request, or remote mutation. Netclaw also rejects an
argument before the phrase completes. A possible local path must stay beneath
the eligible safe root.

Ambient executable configuration is outside this claim. Tool-private cache or
metadata refresh is also outside this claim. The same limit applies to paths
that a tool discovers after execution starts.

The catalog ships with the daemon and changes only through code review. The
agent cannot extend its own auto-pass surface. Every clause needs coverage.
One uncovered clause makes the call prompt.

### How parser facts become one policy result

ShellSyntaxTree describes shell syntax. It does not grant authority. Netclaw
projects those facts into one immutable call-local view and then decides which
authority, if any, covers each command occurrence.

| Step | Input | Output | Owner |
|------|-------|--------|-------|
| Preflight and syntax analysis | Original tool call, shell environment, initial cwd, audience, and run scope | ShellSyntaxTree facts followed by hard deny, path deny, approval mode, or auto allow | ShellSyntaxTree and Netclaw |
| Policy projection | Syntax facts plus the unchanged approval context | Stable call-local candidate IDs and immutable scope facts | Netclaw |
| Grant match | Candidates plus one session/persistent store snapshot | One typed match or one bounded near miss per candidate | Approval actor |
| Coverage | Grant matches, reviewed-safe policy, and an exact one-time retry | One coverage result per candidate | Netclaw |
| Completion | All candidate coverage results | `Allowed`, `RequiresApproval`, or `Denied` | Netclaw |

The coordinator does not rewrite the original command. A prompt and an eventual
execution still refer to the exact tool call the model authored. Candidate IDs
exist only for that call; they are not durable grant IDs.

The coordinator returns one closed result shape:

| Outcome | Consumer data | Next action |
|---------|---------------|-------------|
| `Allowed` | One allow reason and any stored matches that helped cover the call | Execute the original tool call |
| `RequiresApproval` | A narrowed approval context plus any partial stored matches | Prompt only for the uncovered candidates |
| `Denied` | One stable deny reason | Return the denial without execution or prompt |

The important value-domain rules are:

- Effective `Exact` and `FiniteSet` path values pass through `ToolPathPolicy`.
- An `Exact` or `FiniteSet` `AuthoredFileSystemValue` also passes through
  `ToolPathPolicy`. This is the strong parser fact used for a finite loop path.
- `AuthoredPathShape` is lexical evidence only. A slash-shaped value may be a
  repository slug, URL segment, image name, or other data, so shape alone never
  creates filesystem authority.
- `IntegerRange` and `Concatenation` can prove bounded scalar data. They cannot
  select an executable, justify a redirect, or create path authority.
- `Unknown`, incomplete control flow, a dynamic executable, an unresolved path,
  or an unresolved redirect stays strict.

The result can compose. A three-part command can use a stored grant for one
candidate and reviewed-safe policy for the other two. Netclaw prompts only for
the candidates that remain uncovered.

#### Example: bounded status data in a compound command

Input:

```bash
gh run view 123456 --repo example/project --log-failed --verbose 2>&1 \
  | head -200; echo "---EXIT $?---"
```

Assume the operator already stored a global Bash token-prefix grant for
`gh run view`. With a trusted `/work` scope, the facts and result are:

| Candidate | Parser fact | Coverage |
|-----------|-------------|----------|
| `gh run view` | Complete Bash occurrence with canonical tokens | Persistent global `gh run view` grant |
| `head` | Complete occurrence under the real trusted root | Reviewed-safe policy |
| `echo` | Argument is `Concatenation(Exact, IntegerRange(0..255), Exact)` and is not a path | Approval-exempt side effect |

Output: `Allowed`. The status expansion is bounded data; it does not make the
command complex and it does not add filesystem authority. Without the stored
grant, `gh run view` remains uncovered. It accepts `--web`, so the complete
phrase cannot satisfy `ReviewedDiagnostic` without executable-private
flag logic.

If the call is outside every trusted root, the global `gh run view` grant still
matches, while `head` remains uncovered. When an eligible Personal or Team call
names a directory that the session can declare, Netclaw can first return a
`set_working_directory` correction to the agent. Otherwise the uncovered
candidates require approval. The bare `echo` side effect does not become a
reusable prompt choice.

#### Example: a finite Bash loop over known files

Input:

```bash
for f in src/A.cs src/B.cs; do cat /work/$f; done
```

ShellSyntaxTree reports one complete `cat` occurrence. Its effective value is
unknown because Bash can apply runtime word transforms, but its stronger
authored filesystem value is:

```text
FiniteSet("/work/src/A.cs", "/work/src/B.cs")
```

Netclaw checks both values through `ToolPathPolicy`. If both paths are allowed
and a stored grant or reviewed-safe policy covers `cat`, the output is
`Allowed`. A protected path is denied before grant coverage. A non-protected
external path stays exact, but it is not reviewed-safe merely because it is
finite; it needs a folder or global grant that matches, or it requires approval. A
runtime iterator, active glob, or command substitution does not receive this
finite fact.

#### Example: stored mutation grants compose with reviewed-safe readers

Input:

```bash
cd /work && git fetch upstream feature/update 2>&1 | tail -2 \
  && echo "===REMOTE TIP===" && git rev-parse FETCH_HEAD \
  && git log --oneline -3 FETCH_HEAD \
  && echo "===HAS FIX?===" \
  && git show FETCH_HEAD:src/App/App.csproj | grep -n "ProtocolPackage" \
  ; echo "exit: $?"
```

With explicit global grants for `cd`, `git fetch`, `git rev-parse`, `git log`,
and `git show`, the actor covers those exact candidates. It returns `NoGrant`
for `tail` and `grep`; reviewed-safe policy then covers both under `/work`.
Each `echo` occurrence is an approval-exempt side effect, including the final
bounded `Concatenation` that contains `$?`. The final output is `Allowed` with
reason `AllCandidatesCovered`. No single grant covers the compound command.

#### Example: a folder grant near miss

Suppose the store has a Bash token-prefix grant for `git status` under
`/work/project-a`, but the call runs under `/work/project-b`. The actor returns
an `OutsideDirectory` near miss. The candidate remains uncovered, so the final
output is `RequiresApproval`. A same-verb grant is diagnostic evidence, not
authority for a peer directory.

### Persistent approvals

Persistent decisions are stored in
`~/.netclaw/config/tool-approvals.json`:

```json
{
  "version": 3,
  "audiences": {
    "personal": {
      "shell_execute": [
        {
          "shell": "Bash",
          "match": "TokenPrefix",
          "verbTokens": ["git", "push"],
          "directory": "/work/project",
          "createdAt": "2026-08-11T12:00:00+00:00"
        },
        {
          "shell": "Bash",
          "match": "LegacyExact",
          "verb": "dotnet build",
          "directory": null,
          "createdAt": null
        }
      ],
      "notion/create-page": [
        {
          "verb": "create-page",
          "directory": null,
          "createdAt": "2026-08-11T12:00:00+00:00"
        }
      ]
    }
  }
}
```

This file is **not** monitored by the config watcher — writing to it does not
restart the daemon. Each audience has its own section. A token-prefix shell
entry matches the same canonical tokens with optional later tokens. A legacy
entry matches only its exact phrase. Version-2 shell entries convert to
`LegacyExact`, so an upgrade does not add authority.

Use the CLI instead of direct file edits:

```bash
netclaw approvals list
netclaw approvals list --json
netclaw approvals trust-verb "git push" --shell bash
netclaw approvals revoke 'Bash token-prefix "git push" anywhere'
netclaw approvals revoke --tool shell_execute --all --audience personal
```

`trust-verb` accepts one complete static ShellSyntaxTree phrase. It rejects a
flag, redirect, assignment, dynamic command identity, or compound command. For
a non-shell tool, use `--tool`; the CLI stores the text as an exact non-shell
entry. Do not use `--shell` with a non-shell tool.

On the first version-2 load, Netclaw creates a byte-identical
`tool-approvals.json.v2.bak` before it replaces the active file. To recover,
stop the daemon, copy the backup over the active file, and start the current
daemon. The current daemon can convert that backup again. Do not run an old
version-2 daemon against a version-3 file.

Malformed, partial, or future-version files stay untouched. Netclaw marks the
persistent store unavailable. An uncovered call is denied instead of shown as
a normal approval prompt.

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
| Slack | Yes | Block Kit buttons with text-compatible option labels |
| Discord | Yes | Native buttons with text-compatible option labels |
| Mattermost | Yes | Interactive buttons with text-compatible option labels |
| TUI (`netclaw chat`) | Yes | Inline prompt |
| SignalR (web client) | Yes | Inline prompt |
| Headless (`netclaw chat -p`) | No — deny uncovered calls | N/A |
| Reminders | No — deny uncovered calls | N/A |
| Webhooks | No — deny uncovered calls | N/A |

If a channel doesn't support approval, an uncovered call that would require a
prompt is denied with reason `channel_does_not_support_approval`. A stored
grant or an approval-exempt side effect can still cover the call. The
reviewed-safe catalog alone does not grant unattended authority.

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
Near-miss diagnostics are also emitted to the daemon log when a persistent
shell grant almost matches but differs by token phrase, shell, absent folder
scope, directory containment, or symlink safety.

```
Tool executed: shell_execute (approved, pattern=git push)
Tool executed: shell_execute (PreviouslyApproved, pattern=git push [persistent: git push in /home/user/repo])
Tool denied: shell_execute (denied_by_user, pattern=docker rm)
Tool denied: shell_execute (timed_out, pattern=kubectl apply)
```

Each shell decision also emits ordered `Shell policy trace:` rows to the daemon
log. A row contains only enum facts, a call-local candidate ID, a bounded and
redacted executable basename, coverage kind, scope relation, and grant time. It
does not contain the full command, arguments, raw paths, tokens, redirects,
secrets, or model content. The trace never enters the prompt or session journal.

Example for the compound status command above:

```text
Shell policy trace: stage=StoredGrantMatch outcome=Covered reason=PersistentGlobalGrant candidate_id=0 executable=gh coverage=PersistentGlobal scope_relation=Global grant_timestamp=2026-08-13T00:00:00.0000000+00:00
Shell policy trace: stage=StoredGrantMatch outcome=Uncovered reason=NoGrant candidate_id=1 executable=head coverage=Uncovered scope_relation=None grant_timestamp=(null)
Shell policy trace: stage=ReviewedSafePolicy outcome=Covered reason=ApprovalExemptSideEffect candidate_id=2 executable=echo coverage=ReviewedSafePolicy scope_relation=None grant_timestamp=(null)
Shell policy trace: stage=ReviewedSafePolicy outcome=Covered reason=ReviewedSafePhrase candidate_id=1 executable=head coverage=ReviewedSafePolicy scope_relation=UnderRealRoot grant_timestamp=(null)
Shell policy trace: stage=Completion outcome=Allow reason=AllCandidatesCovered candidate_id=(null) executable=(null) coverage=(null) scope_relation=None grant_timestamp=(null)
```

Read a trace from the final row backward:

1. `Completion/RequiresApproval/UncoveredCandidates` means at least one
   candidate lacked coverage.
2. Find that candidate ID in `StoredGrantMatch`. `NoGrant` means no same-call
   grant matched. `TokenMismatch`, `ShellMismatch`, `OutsideDirectory`, or
   `Symlink` explains a bounded near miss.
3. Check whether that ID has a `ReviewedSafePolicy` or `OneTimeApproval` row.
   If it does, that stage supplied coverage after the grant check.
4. `Completion/Deny/InternalPolicyFailure` is not an ordinary approval case.
   Treat it as a policy defect or malformed internal result; do not add a grant
   to work around it.
5. `Trace/TraceTruncated/TraceLimitReached` means the diagnostic row cap was
   reached. It never changes the authorization result.

Use the trace to classify a repeated prompt before a policy change:

- A valid same-phrase folder near miss usually indicates scope or cwd drift.
- `NoGrant` plus a reviewed-safe candidate outside a trusted root usually points
  to project/scratch alignment.
- A complete candidate with no general safe proof is an expected approval.
- A command that should have a strong parser fact but remains unresolved is a
  ShellSyntaxTree evidence gap, not a reason to add executable-specific Netclaw
  parser logic.

## FAQ

**Q: Can I disable approval gates entirely?**
A: Yes. Set an exact `shell_execute` override to `Auto`. Removal alone does not
disable the Personal shell default. If the exact override is absent, the shell
stays fail-closed in `Approval` mode.

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
A: Yes. Use `netclaw approvals trust-verb`, or use the agent normally and
select an always option. Use `netclaw approvals list` to copy the exact label
for a later revoke.
