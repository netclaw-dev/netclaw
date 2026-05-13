## Context

`tool-approval-gates` (originally specified in `2026-04-29-tool-approval-gates`, extended in `2026-05-07-directory-scoped-approval-patterns`) shipped a flat-string approval store keyed by audience and tool name. Each pattern is a string that the matcher inspects at evaluation time to decide whether it represents a verb chain, a normalized full command, a directory root, or a bash fragment. The shape works in trivial cases and fails in non-trivial ones: complex bash blocks shred into junk fragments that get persisted as approvals; the matcher's "is this a directory? a verb?" heuristic depends on trailing slashes; the channel adapters render two parallel sections (`Patterns` and `Directory Roots`) because the data model can't distinguish them.

Real use surfaced two outcomes neither the spec nor the original PRs anticipated:

1. **Approval volume is too high.** The agent's only declared safe space is `~/.netclaw/sessions/<id>/` (the per-session scratch dir established by `SessionMessageAssembler`). Any shell call against the user's actual project — typically read-only `grep`/`ls`/`cat`/`git status` — triggers the approval gate. Users running `netclaw` against a repository they own end up clicking through dozens of prompts that have no security value.
2. **Approval clarity is too low.** Aaron's persisted store accumulated 50 entries including `done`, `for pid`, `awk {print $2})`, `do threads=$(grep`, `fds=$(ls`. None of those will ever match a sensible future invocation. The user sees them in `netclaw approvals list` and cannot reason about what they're authorizing or revoking.

We have no users yet, so we can do a clean redesign. This change replaces the flat-string store with a typed `(verb, directory)` model, layers a safe-verbs ∩ safe-space short-circuit on top, and rebuilds the prompt UX so a single click maps to a single decision.

The session already has the right primitives. `WorkingContext` (`src/Netclaw.Actors/Sessions/WorkingContext.cs`) persists a `ProjectDirectory` set via the `set_working_directory` tool, surviving compaction and daemon restart. `ScopedFileAccessPolicy` (`src/Netclaw.Actors/Tools/ScopedFileAccessPolicy.cs`) and `ToolAudienceProfileResolver` already implement audience-aware root resolution with symlink-segment protection for file_read. We're adopting that pattern for shell.

## Goals / Non-Goals

**Goals:**

- Reduce approval prompt volume to commands that genuinely warrant interrupting the user (mutation, or anything outside declared safe spaces).
- Make every prompt the user sees answer one obvious question: *"approve `<verb>` in `<directory>`?"*.
- Type the approval store so each entry self-describes its scope — verb plus directory, with an explicit `null` for the global wildcard.
- Stop persisting bash fragments and over-precise normalized commands. Approvals should be coarse enough to reuse and specific enough to reason about.
- Give scheduled/unattended tasks a clean path to pre-approval that doesn't require hand-editing JSON.
- Push the agent toward calling `set_working_directory` early when working on a project, so the trust boundary is correctly declared.

**Non-Goals:**

- Migration of the existing v1 store. Quarantine to `.v1.bak` and start fresh; users have no production approvals worth preserving.
- Glob or regex pattern matching. Verb chains and absolute directory paths only.
- Stale-entry pruning of the v2 store. Track separately if it becomes a real problem.
- Persistent "trust this folder forever" grants beyond what `(verb, directory)` already expresses.
- Rewriting `ShellTokenizer` to be a real bash parser. We add cheap structural detection that refuses pattern extraction when the input is messy; we do not attempt to understand for-loops, subshells, or heredocs semantically.
- Modal-driven scope picker. Considered and rejected — see Decision 6.

## Decisions

### 1. Trust boundary is `safe verb ∩ safe space`, not just one or the other

**What:** Three-position policy. Auto-run with no prompt only when the verb is on a curated per-OS safe-verbs list AND the cwd resolves under one of the audience-aware safe-space roots (`session_dir`, `project_dir` for Personal/Team, `session_dir` only for Public). Anything else prompts. Hard-deny list (layer 1) is unchanged.

**Why:** A pure "in safe space → run anything" model auto-allows mutation (`git push` in your repo still pushes to the world). A pure "verb is read-only → run anywhere" model is the curated-allowlist pattern we explicitly rejected when we built this in the first place. The intersection captures the right thing: read-only inspection of declared work surfaces is implicit; everything else is explicit.

**Alternatives considered:**

- *In safe space → run anything (subject to hard-deny):* Too loose. `git push` in a project dir bypassing approval is an obvious foot-gun.
- *Verb on safe list anywhere → run:* Re-introduces the global verb allowlist. The `freshdesk` case (a user-installed CLI we know nothing about) shows this is actually attractive for some commands but should be opt-in via `trust-verb`, not a default.
- *Per-session "first prompt then cache":* Burns the user's attention in the first 5 minutes of every session. Doesn't survive `set_working_directory` being a thing.

### 2. Safe-verbs list is per-OS, file-driven

**What:** `safe-verbs.linux.json` and `safe-verbs.windows.json` shipped with the daemon. Each is a flat list of verb chains. Users can override at `~/.netclaw/config/safe-verbs.<os>.json`.

**Why:** The list of read-only-by-nature verbs differs sharply between OSes — `dir`/`type`/`Get-Content` on Windows vs. `ls`/`cat`/`find` on POSIX. A single combined list either bloats unnecessarily or omits things users will hit. Per-OS keeps the defaults focused.

**Alternatives considered:**

- *Single combined list:* Bloats; verbs that don't exist on the target OS are dead weight in the matcher.
- *Hardcoded constants:* No user override path. Users can't add their own internal CLIs without a code change.
- *Config in `netclaw.json`:* Mixes operational config (hot-reloaded) with security policy (should not be silently swappable).

Default Linux/macOS list: `ls`, `find`, `grep`, `egrep`, `fgrep`, `rg`, `cat`, `head`, `tail`, `wc`, `sort`, `uniq`, `cut`, `tr`, `awk`, `sed -n`, `file`, `pwd`, `which`, `stat`, `tree`, `du`, `df`, `git status`, `git log`, `git diff`, `git show`, `git branch`, `git remote`, `git rev-parse`, `git ls-files`, `git blame`.

Default Windows list: `dir`, `type`, `more`, `where`, `findstr`, `Get-ChildItem`, `Get-Content`, `Select-String`, `Get-Item`, `Test-Path`, `Get-Location`, `Resolve-Path`, plus the same git read subcommands.

`sed -n` is intentional — `sed -i` mutates files. `awk` is on the list because no flags currently mutate; if that ever changes the gate is structural (verb-chain match, not `awk -i`).

### 3. Approval atom is `(verb, directory)`, both required, directory may be null

**What:**

```csharp
public sealed record ApprovalEntry
{
    public required string Verb { get; init; }       // "git remote", "freshdesk"
    public string? Directory { get; init; }          // absolute path, or null = anywhere
}
```

The on-disk schema bumps to `version: 2`. v1 files are quarantined to `.v1.bak` on first read; the daemon writes a fresh empty v2 file.

**Why:** Every persistent approval needs to answer two questions: *what verb* and *where*. Today the store collapses both into a single string and tries to recover them at evaluation time. The recovery is fragile (trailing-slash check tells the matcher "this is a directory") and the result reads as line noise to humans. Typing the entry kills both problems.

`directory: null` is the explicit global wildcard. It exists for cases like the `freshdesk` CLI where the user genuinely wants the verb to run anywhere — typically scheduled or unattended invocations where the cwd will vary across firings.

**Alternatives considered:**

- *Separate buckets per shape:* `verb_in_directory: [...]` and `verb_anywhere: [...]`. Verbose; same information, more ceremony.
- *String convention with separator:* `"git remote@/abs/path/"`. Stringly-typed; will eventually grow ambiguity.
- *Auto-translate v1 → v2:* Too many shapes are unrecoverable (bash fragments, normalized commands with embedded args, bare directory roots without verbs). Honest quarantine + clean slate is safer.

### 4. `ShellTool` cwd defaults to `project_dir` then `session_dir`, never daemon cwd

**What:** When the model omits `WorkingDirectory`, `ShellTool` resolves cwd in priority order: `project_dir` (from `WorkingContext`) if set, else `session_dir`. Today the code at `src/Netclaw.Actors/Tools/ShellTool.cs:81-82` falls through to `ProcessStartInfo`'s default — the daemon process's cwd, which is wherever the daemon happened to be launched.

**Why:** The daemon-cwd default is a footgun. It can be `/`, `~/.netclaw`, anywhere — completely unrelated to what the agent is "working on." Approval policy can't reason about it; the user can't predict it. Forcing the cwd into a declared safe space makes the trust boundary structural: every shell call has a known parent that's either inside a safe space or explicitly elsewhere.

**Alternatives considered:**

- *Require the model to always pass `WorkingDirectory`:* Brittle; the model frequently omits it for short commands.
- *Default to `session_dir` only:* Loses the work the user did when they (or the agent) called `set_working_directory`.

### 5. `ShellTokenizer` refuses to extract patterns from messy input

**What:** When `SplitCompoundCommand` encounters bash control-flow tokens (`for`, `while`, `do`, `done`, `then`, `fi`, `case`, `esac`) or unbalanced quotes/brackets, it returns an empty verb-chain list. The approval gate offers only `Once` and `Deny` — no persistent grant — and the prompt body shows "complex command — only one-shot approval available."

**Why:** Today's splitter only knows `&&`/`||`/`;`. Anything else gets treated as a single segment, normalized, and shoved into the patterns list. That's how `done`, `for pid`, and `awk {print $2})` end up in Aaron's store. Refusing to extract on detection is the cheap, safe answer — we don't pretend to understand the command, we just refuse to remember it.

**Alternatives considered:**

- *Best-effort extraction with junk filtering:* Risks drift. The list of "things that look like junk" is open-ended.
- *Real bash parser:* Out of scope. Our needs are bounded by "is this clean enough to remember"; we don't need to interpret.

### 6. Five-button prompt, no modal

**What:** Approval prompt presents `Once`, `This chat`, `Always here`, `Always anywhere`, `Deny` as five buttons in one row. `Always anywhere` and `Deny` use the platform's danger styling (`style: "danger"` on Slack, `ButtonStyle.Danger` on Discord).

**Why:** A four-button prompt with a modal on `Approve always` was considered for elegance — the elevated decision (in this folder vs anywhere) gets a deliberate confirmation step. But the state-management cost is real: ~200–300 lines per channel adapter for the round-trip handler, a new "scope chosen" follow-up message in the protocol, and additional failure modes (user dismisses modal without submitting, daemon restart between original click and modal submit). Five buttons collapse all of that to one click and one persist decision per button.

The danger-styled `Always anywhere` button is the mitigation for the "fat-finger" risk: it reads visually distinct, matching `Deny`. Users who want to elevate a grant to global can do so with one deliberate click; the rare nature of the case is reflected in the styling, not the click count.

Slack and Discord both cap at 5 buttons per row, so we are at the ceiling. A sixth button would need either a row split (changes the visual hierarchy) or an overflow menu (less obvious). We don't expect to need a sixth.

**Alternatives considered:**

- *4 buttons + modal on Approve always:* Elegant, expensive. See above.
- *4 buttons, drop "This chat":* Cleaner row but loses a useful intermediate scope. Some users debug iteratively across many similar commands and don't want to commit to "always."
- *Single approve button + scope dropdown:* Slack supports it but the UX feels indirect ("pick from this menu, then click the approve button you already clicked").

### 7. Compound commands group by cwd, persist as a batch

**What:** When the model issues `cmd1 && cmd2 && cmd3`, the matcher extracts every verb chain and presents them as bullets in a single prompt. One click on `Always here` persists `(verb, cwd)` for each verb in one shot. Cross-directory compounds (rare) get treated as one prompt scoped to the cwd; if the user wants finer control, they Deny and let the agent split.

**Why:** Forcing the agent to run one verb per call means N prompts for one logical operation — annoying. Splitting at our layer would need cross-call state to reconstruct the user's intent, which we don't have. Letting the user approve once for the whole compound matches how they actually think about the operation ("yes, do all three of those things").

**Alternatives considered:**

- *One prompt per verb:* User-hostile. Three prompts for `git fetch && git rebase && git status`.
- *Refuse compound outside safe space:* Forces the agent to issue one at a time. Cleaner per-prompt, but multiplies prompts when the user is actively working.

### 8. `netclaw approvals trust-verb <verb>` is the only path to global grants

**What:** `Always anywhere` in the prompt and `netclaw approvals trust-verb <verb>` in the CLI both write `(verb, null)` to the store. The CLI is the deliberate, scriptable path; the prompt is the in-the-moment path. Both flow through `ToolApprovalStore.AddApproval` with the same comparer.

**Why:** Scheduled tasks need pre-approval (the schedule fires unattended; nobody can click). Hand-editing JSON is the current state and it's the source of `done`/`for pid` style entries. A typed CLI command makes the intent explicit and the audit trail visible.

The agent uses this from inside a session as well: at schedule-creation time, when it identifies that an unattended task will need a verb to be globally approved, it asks the user and (on confirmation) calls the equivalent action.

**Alternatives considered:**

- *Daemon RPC instead of CLI shelling:* Cleaner, but requires a new RPC surface. Defer until we have other RPC needs.
- *Implicit auto-trust for verbs the agent calls during schedule setup:* Way too magic. Users should know what's being globally trusted.

### 9. Reuse `ScopedFileAccessPolicy` infrastructure

**What:** A new `ScopedShellSafeVerbPolicy` mirrors the `ScopedFileAccessPolicy` shape. Both use `ToolAudienceProfileResolver` for root resolution and `ContainsSymlinkSegment` for symlink-segment defense.

**Why:** The audience model (Personal/Team/Public) and the symlink-segment guard are well-tested and battle-hardened. Re-implementing them for shell would be duplicate code that drifts. Public audience inherits the same `session_dir`-only restriction file_read enforces — Public sessions can never auto-allow shell against `project_dir` even when set.

### 10. Resolution message replaces dual sections with one line

**What:** Today's resolution message has separate `Patterns` and `Directory Roots` sections. New format is one line:

- `Saved: jsonlint, git pull, git rev-parse in ~/repos/foo/`
- `Saved: freshdesk anywhere`
- `Saved for this chat: jsonlint in ~/repos/foo/`
- `Approved (no save)` — for Once
- `Denied`

**Why:** The two-section format is the on-screen artifact of the data-model conflation in v1. Once the entries are typed, the rendering simplifies. One line is enough; the verbs and the scope are both present and unambiguous.

## Risks / Trade-offs

- **Risk: safe-verbs list drifts from reality.** A new tool (`rg`, `delta`, `eza`) ships and isn't on the list, so users go through approval friction we didn't intend. → Mitigation: user-overridable file at `~/.netclaw/config/safe-verbs.<os>.json`. Update default lists at release boundaries based on observed friction.
- **Risk: a verb on the safe list turns out to have a mutating mode.** `awk -i inplace` mutates; if `awk` is on the list and we match by verb chain, we'd auto-allow it. → Mitigation: verb-chain matcher pins to `awk` (no flags); safe-list entries that need flag pinning use the verb+subcommand form (`sed -n`, not `sed`). Audit the list at definition time, document the rationale next to each entry.
- **Risk: `project_dir` set incorrectly auto-allows too much.** User opens a session, agent guesses wrong, calls `set_working_directory ~/`. Now the entire home dir is "safe." → Mitigation: the safe-verbs list is the second axis — even with `~/` as project_dir, only read-only verbs auto-run. Mutation still prompts. The eval cases (positive + negative) explicitly cover this. AGENTS.md guidance anchors on intent ("you're working on a specific codebase"), not on dodging approvals.
- **Risk: bash-fragment refusal annoys users with legitimate complex commands.** Someone writes `for f in *.log; do grep ERROR "$f"; done` and gets only `Once`/`Deny`. → Mitigation: this is the right answer. We can't reason about persistent grants for control-flow we don't parse. The user can split the command into one-shot pieces or, for repeated needs, register the inner verb (`grep`) globally via `trust-verb`.
- **Risk: 5 buttons feel cluttered on narrow Slack channels.** Mobile especially. → Mitigation: revisit if observed. The terse labels (`Once`, `This chat`, etc.) keep the row width down; danger styling visually breaks the row into "safe" and "powerful" halves.
- **Risk: agent regresses on `set_working_directory` adoption after AGENTS.md change.** Eval suite catches this on every PR. Positive case asserts the call happens early; negative case asserts no preemptive call when there's no project signal; recovery case asserts the failure-path hint is read and acted on.
- **Risk: daemon restart kills pending approval prompts (existing bug, not introduced here).** Aaron flagged this independently — clicking an approval button after a restart hits a dead actor. Out of scope for this change but compounds with the new prompt UX. Track separately.
- **Trade-off: breaking change wipes the v1 store.** No users in production yet, so the cost is bounded. We document the quarantine clearly so users who manually curated their v1 store can mine it for ideas.
- **Trade-off: `Always anywhere` is one click away.** Mitigated by danger styling, but a determined misclick is still possible. CLI-only would be safer; we chose the in-prompt path because the friction of "pop out to a terminal" defeats the purpose during active sessions.

## Migration Plan

This is a breaking change with no data migration. Deployment:

1. Daemon upgrades. On first read of `~/.netclaw/config/tool-approvals.json`, the loader checks for `version: 2`. If absent or non-2, the file is moved to `tool-approvals.json.v1.bak` and the loader returns an empty v2 store.
2. The daemon writes a fresh `tool-approvals.json` with `{"version": 2, "audiences": {}}` on the first persist call.
3. The next `netclaw approvals list` invocation surfaces a one-line note: "Your previous approvals (N entries) were quarantined to ~/.netclaw/config/tool-approvals.json.v1.bak during a schema upgrade. Inspect or restore manually if needed."
4. Users who relied on specific v1 entries re-establish them via the new prompts or `netclaw approvals trust-verb <verb>` for global grants.

Rollback: revert the daemon. The v1 file is intact at `tool-approvals.json.v1.bak` — operator can rename it back. Approvals written under v2 are lost on rollback, which matches the breaking-change posture.

## Open Questions

- **Verb-chain granularity for compound subcommands.** `git remote` vs `git remote get-url` — today's matcher pins to verb + first subcommand (`git remote`). Should `git remote get-url` be a distinct grant from `git remote add`? Probably not for v1 (the existing granularity is fine), but worth revisiting if users complain that one approval is doing too much.
- **`netclaw approvals trust-verb` confirmation UX.** Should the CLI prompt for confirmation when adding a global wildcard, or trust the explicit command name? Current call: trust the command name (no extra confirm). Revisit if accidental adds become a pattern.
- **Resolution message edit-in-place vs. new message.** Slack supports `chat.update` to edit the original prompt; Discord similar. Editing in place feels cleaner than appending a new "resolved" message. Open: verify both platforms behave correctly when the resolution message arrives after the prompt has been thread-quoted by another reply.
- **Eval case: what counts as a "project signal"?** The positive eval asserts the agent calls `set_working_directory` "early" when the user mentions a repo path. We need an explicit threshold for the eval — first user message? First three turns? — so the assertion isn't ambiguous.
