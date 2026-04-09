## Context

Netclaw's tool security model is binary per tool per audience — a tool is either
granted or not. Once `shell_execute` is granted (Personal audience +
`ShellExecutionMode.HostAllowed`), there is zero command-level filtering beyond
`ToolPathPolicy`'s path-based heuristics. The March 27 incident demonstrated
this gap: the agent ran `netclaw daemon stop` and killed its own host process.

The current enforcement pipeline is:

1. `ToolAccessPolicy.AuthorizeInvocation()` → `ToolAccessDecision` (Allow/Deny)
2. `DispatchingToolExecutor` throws `ToolAccessDeniedException` if denied
3. `ShellTool.ExecuteAsync` checks `ToolPathPolicy.CommandReferencesDeniedPath`
4. Shell executes with no command-verb filtering

Claude Code solves this with verb-chain prefix matching: `git push` approves all
`git push` variants. Users approve on first use and can persist approvals. This
design adapts that model for Netclaw's multi-channel, multi-audience architecture.

Key constraints:
- Approval is infrastructure-driven, transparent to the LLM
- Every channel (Slack, TUI, plain text) must support the approval interaction
- Persistent approvals must NOT live in `netclaw.json` (config file changes
  trigger `ConfigWatcherService` and daemon restart)
- The approval system must be tool-agnostic (shell first, MCP and others later)
- Single-process Akka.NET actor model; approval waits happen on thread pool
  tasks, not by blocking the actor mailbox

## Goals / Non-Goals

**Goals:**

- Add a configurable hard deny floor for categorically dangerous commands
- Add per-audience tool approval configuration (Auto/Approval/Deny per tool)
- Implement mid-turn approval pause that blocks individual tool tasks without
  blocking the session actor or other parallel tool calls
- Define a general `ToolInteractionRequest`/`ToolInteractionResponse` protocol
  that channels render using their shipped approval UX (MVP Slack text A/B/C
  replies)
- Build shell-specific command-pattern matching via `IToolApprovalMatcher` with
  verb-chain prefix extraction, compound command splitting, and `bash -c`
  recursion
- Store persistent approvals per audience in a separate file that does not
  trigger daemon restart
- Integrate with init wizard for per-audience shell approval mode selection

**Non-Goals:**

- Broader approval semantics for non-shell tools beyond the currently shipped
  matcher infrastructure
- Sandboxed shell execution (reserved behind `ShellExecutionMode.SandboxOnly`)
- Hot-reload of approval config (file read at startup; new approvals written
  immediately but existing sessions use their cached copy)
- Approval for non-tool operations (memory writes, compaction, etc.)
- Interactive confirmation modals or multi-step approval workflows

## Decisions

### Decision 1: Three-layer invocation pipeline

**Choice:** Add approval as a third layer after hard deny and tool access.

```
Hard Deny → Tool Access → Approval Gate → Execute
```

**Alternatives considered:**
- Single layer combining deny + approval: rejected because hard deny must be
  non-overridable, while approval is interactive. Mixing them confuses
  operator intent.
- Approval before tool access: rejected because tool access already handles
  audience-level filtering. Consulting approval for tools the audience can't
  use wastes effort.

**Rationale:** Each layer has a distinct security posture: hard deny (never
allowed), tool access (allowed or not per config), approval (allowed but needs
human sign-off). Separating them makes each independently testable and
configurable.

### Decision 2: Infrastructure-driven, LLM-transparent

**Choice:** The LLM never knows approval is happening. It calls
`shell_execute`, gets either a result (approved) or a denial error (denied).
The pause happens inside the tool execution pipeline.

**Alternatives considered:**
- Agent-invoked approval tool (`request_approval`): rejected because the LLM
  could skip the tool or manipulate the approval reason. Security-critical
  behavior must not depend on LLM compliance.
- LLM-mediated text approval (LLM posts "I'd like to run X", user replies):
  rejected because it relies on LLM correctly interpreting user responses.

**Rationale:** The approval flow is a transparent interceptor. The LLM's tool
contract is unchanged. This is simpler, more secure, and doesn't add a new tool
to the LLM's context.

### Decision 3: TaskCompletionSource-based mid-turn pause

**Choice:** When a tool requires approval, its `Task` in the parallel
`Task.WhenAll` batch blocks on a `TaskCompletionSource<ApprovalDecision>`.
The session actor completes the TCS when an approval response arrives.

**Alternatives considered:**
- Return "needs approval" as a tool result, let LLM retry next turn: rejected
  because it requires two turns and the LLM might not retry or might retry
  incorrectly.
- Serialize all tool calls when any needs approval: rejected because it delays
  safe tools unnecessarily. Non-approval tools should complete in parallel.
- Return from pipeline, re-invoke later: rejected because it requires holding
  pending tool calls in actor state and re-dispatching, adding state machine
  complexity.

**Rationale:** TCS blocking is the simplest mechanism. The blocked task sits on
the thread pool consuming no CPU. Other tasks complete independently. The
session actor remains responsive to its mailbox (including the approval
response). The `Task.WhenAll` in `SessionToolExecutionPipeline.ExecuteToolsAsync`
naturally waits for all tasks, including the approval-blocked one.

**Actor boundary implications:** The `IApprovalChannel` is created by the session
actor and passed into the pipeline. The actor owns the TCS dictionary. When a
`ToolInteractionResponse` message arrives during the Processing behavior, the
actor looks up the TCS by `CallId` and completes it. This preserves the actor's
single-threaded mailbox model because the pipeline task is waiting asynchronously,
not on the actor thread.

**Failure mode:** If the approval response never arrives, a configurable timeout
(default: 5 minutes) on the TCS causes the task to unblock with
`ApprovalDecision.TimedOut`, which is treated as a deny. The tool result
includes a timeout explanation.

### Decision 4: ToolInteractionRequest/Response as current approval protocol

**Choice:** Define `ToolInteractionRequest` (session output) and
`ToolInteractionResponse` (session command) with an interaction `Kind`, but keep
the change scoped to the currently shipped approval interaction.

**Alternatives considered:**
- Approval-specific `ApprovalRequestOutput`/`ApprovalResponse`: rejected because
  the shipped protocol already exists and is sufficient for MVP.

**Rationale:** The shipped protocol already covers the approval flow without
expanding scope. The important property for MVP is that the session can emit a
prompt and receive a keyed response that unblocks the waiting tool task.

### Decision 5: Channel capability determines approval behavior

**Choice:** Channels declare `SupportsInteractiveApproval`. If a channel does
not support it, approval-gated tools are auto-denied immediately (no hang,
no timeout).

**Alternatives considered:**
- Timeout-based degradation: rejected per constitution ("no silent fallbacks").
  Hanging until timeout hides the misconfiguration.
- Auto-approve for unsupported channels: rejected because it bypasses the
  security intent of approval mode.

**Rationale:** Fail loudly. If the operator enables approval mode on a channel
that can't support it, `netclaw doctor` warns at startup. At runtime, the
tool is denied with reason `channel_does_not_support_approval`. The LLM gets
a clear error.

### Decision 6: Persistent approvals behind actor-backed service

**Choice:** Store persistent approvals in
`~/.netclaw/config/tool-approvals.json`, not in `netclaw.json`, and mediate
lookup/recording through actor-backed `IToolApprovalService`.

**Alternatives considered:**
- Store in `netclaw.json` under `Tools.ShellApprovals`: rejected because
  `ConfigWatcherService` monitors `netclaw.json` for changes and triggers
  daemon restart. Every "Approve Always" click would restart the daemon.
- Store in SQLite database: rejected as over-engineered for a simple JSON list.

**Rationale:** The separate file is not watched by `ConfigWatcherService`.
`DispatchingToolExecutor` asks `IToolApprovalService` which patterns remain
unapproved for the current call, and `LlmSessionActor` records Approve For This
Chat / Approve Always decisions through the same service. Approve Once is kept
on the in-memory retry path only. This matches the shipped single-writer actor
boundary and keeps approval state out of `ToolAccessPolicy`.

**File format:**
```json
{
  "personal": {
    "shell_execute": ["git push", "git add", "dotnet build"]
  },
  "team": {
    "shell_execute": ["git status"]
  }
}
```

Per-audience sections. Shell uses pattern lists. The current MVP behavior is
driven by the shipped shell/text approval flow.

### Decision 7: Verb-chain prefix extraction for shell patterns

**Choice:** Tokenize the command, extract non-flag tokens until the first
flag (`-`) or path/URL argument. The resulting verb chain is the pattern.

```
"git push origin main"       → "git push"
"docker compose up -d"       → "docker compose up"
"ls -la /tmp"                → "ls"
"kubectl delete pod my-pod"  → "kubectl delete"
```

**Alternatives considered:**
- Full command string as pattern: rejected because patterns would never match
  twice (arguments vary per invocation), causing the approval list to explode.
- Single verb only: rejected because `git push` and `git status` have very
  different risk profiles. Two-token depth is the sweet spot for most tools.
- Regex patterns: rejected as too complex for operators to manage.

**Rationale:** Verb chains identify intent. `git push` means "push to remote"
regardless of branch or remote name. A typical workflow produces ~10 patterns
after a week of use.

### Decision 8: Configurable hard deny list with sensible defaults

**Choice:** Ship compiled defaults (self-destruction patterns). Operators can
add or remove patterns via `ToolConfig.HardDenyPatterns` in `netclaw.json`.

**Defaults:**
- `netclaw daemon stop`, `kill`/`killall`/`pkill` targeting netclaw processes,
  `systemctl stop netclaw`
- `rm -rf /`, `rm -rf ~/`, `rm -rf $HOME`
- Fork bombs, `mkfs`

**Alternatives considered:**
- Compiled-only deny list (no config override): rejected because operators
  may have legitimate patterns to add or edge cases to exclude.
- No hard deny (approval handles everything): rejected because self-destructive
  commands should never be approvable. The agent killing its own host is a
  categorically unacceptable outcome.

**Rationale:** Hard deny is the security floor. It's checked even in
`HostAllowed` mode (no approval config). Configurable defaults balance safety
with operator flexibility.

### Decision 9: IToolApprovalMatcher as extension point

**Choice:** Define `IToolApprovalMatcher` interface with `ExtractPattern`,
`IsApproved`, and `FormatForDisplay`. Shell implements verb-chain matching.
Default implementation uses tool name only.

**Rationale:** Keeps shell-specific logic out of the general approval
infrastructure. Future tool types (database query tools, etc.) can provide
their own matchers without changing the approval pipeline. For v1, only
`ShellApprovalMatcher` and `DefaultApprovalMatcher` are implemented.

### Decision 10: Per-audience approval configuration

**Choice:** `ToolApprovalConfig` lives on each `ToolAudienceProfile`, not
globally on `ToolConfig`. Each audience has its own `DefaultMode`, its own
`ToolOverrides`, and its own persistent approval list.

**Rationale:** Different audiences have fundamentally different trust levels.
Personal may want approval for shell. Team may want shell off entirely. Public
definitely has shell off. Per-audience approval naturally composes with the
existing per-audience tool access model.

### Decision 11: Compound command batched approval prompt

**Choice:** Split compound commands on `&&`, `||`, `;`, `|`. Check each
segment. Collect all unapproved patterns into one prompt. User approves or
denies the batch. Deny = deny the entire compound command.

**Alternatives considered:**
- Per-segment individual prompts: rejected as too chatty.
- Approve the full compound as one pattern: rejected because
  `git status && git push --force` has very different risk per segment.

**Rationale:** One prompt, one user decision. Show full command for context,
list unapproved patterns. If the user wants granular control, they run
commands separately.

## Risks / Trade-offs

- **First-week friction**: Before the approval list builds up, every new
  command pattern triggers a prompt. → Mitigation: Ship a starter set of
  pre-approved safe patterns (read-only commands like `ls`, `cat`, `git log`,
  `git status`, `git diff`). Document the ramp-up in onboarding.

- **Approval timeout during long operations**: If the user is away when an
  approval prompt fires, the tool times out and the LLM gets a confusing
  denial. → Mitigation: Configurable timeout (default 5 min). The denial
  message explicitly says "approval timed out" so the LLM can inform the user.

- **Thread pool thread blocked during approval wait**: The tool task sits on
  the thread pool awaiting approval. In pathological cases (many concurrent
  approval waits across sessions), this could exhaust the thread pool. →
  Mitigation: The wait is async (`await` on TCS), not a blocking wait.
  No thread pool thread is consumed while waiting.

- **Pattern matching bypasses**: `bash -c`, encoding, aliasing. → Mitigation:
  Recursive `bash -c`/`sh -c` scanning. Accept that static analysis is
  defense-in-depth, not bulletproof (documented in `ToolPathPolicy` already).
  The audience trust model is the primary security boundary.

- **Stale persistent approvals**: Operator approves a pattern, later decides
  it's too broad. → Mitigation: Operators can edit
  `tool-approvals.json` directly.

- **Slack text reply parsing**: Slack must associate an A/B/C reply with the
  correct pending approval and requesting user. → Mitigation:
  `SlackThreadBindingActor` keeps pending requests for the thread, only accepts
  matching requester replies, and forwards a keyed `ToolInteractionResponse`
  through the session pipeline.

## Open Questions

- Should the starter set of pre-approved safe patterns be configurable per
  audience, or always include read-only commands like `ls`, `git log`, etc.?
