# Continuation Memory: approval-policy-path-extraction → trust-zones rewrite

This is a hand-off note from a long working session (Aaron + Claude, 2026-05-09).
Read it end-to-end before doing anything in `openspec/changes/approval-policy-path-extraction/`.
The architectural conclusion of that session **invalidates** large parts of the existing change
proposal/design/specs/tasks. Don't just `/opsx-apply` against them; we need to rewrite first.

---

## Where the code is right now

**Branch:** `openspec/approval-policy-v2`. Pushed to `origin/openspec/approval-policy-v2`. PR #940.
The branch name is now misleading (covers v2 + v2.1 + a fresh architectural rewrite about to happen)
but git history is preserved that way.

**Last pushed commit:** `579a4f6e fix(approvals): side-effect candidates auto-allow at match time`.

**Daemon state:** Aaron's local netclawd is currently running this commit (binary swap done).
`~/.netclaw/config/tool-approvals.json` has real entries from his dogfooding — see "Live evidence" below.

**Uncommitted working tree at session end:**
```
M src/Netclaw.Actors/Sessions/LlmSessionActor.cs
M src/Netclaw.Actors/Tools/ToolAccessPolicy.cs
M src/Netclaw.Channels.Discord/DiscordApprovalPromptBuilder.cs
M src/Netclaw.Channels.Slack/SlackApprovalBlockBuilder.cs
M src/Netclaw.Security.Tests/ShellApprovalMatcherTests.cs
```

These uncommitted edits add: `AllCandidatesResolveToSessionScratch` button-row guard,
session-scratch persistence guard in `LlmSessionActor.PersistApprovalCandidatesAsync`, the
`ResolveHeaderLocation` helpers in both channel builders that show the *target* directory
in the approval header instead of cwd, and a regression test for cd-target-as-directory.

**These uncommitted edits are tactical patches for the v2.1 model that the upcoming rewrite is
about to throw out.** Recommendation: `git restore .` and start the rewrite from a clean working
tree. The session-scratch ideas remain conceptually relevant but the trust-zone rewrite makes
session_dir's role different (it becomes one trusted root among several), so the patches won't
slot into the new model unchanged. Save brain energy by deleting them.

---

## Architectural conclusion of the session

The session started doing path-extraction-style fixes on v2 and ended at a fundamental rewrite
of the approval model. The conclusion is non-negotiable; Aaron drove it. Don't try to relitigate.

**The new model:**

### Trust zones, not session state

Approval reasoning anchors on **trusted zones**, which are defined as:

1. **Audience config** — read-allowed (and write-allowed) roots declared per-audience in the
   trust profile. Static, operator-owned. Personal might have `/`, Team might have
   `~/work-projects/*`, Public has `session_dir` only.
2. **Session directory** — `~/.netclaw/sessions/<id>/` is *always* a trusted zone.
3. **Operator-extended zones** — directories the user clicked "always" on in past prompts,
   persisted per-audience.

Anything inside any of these = silent (subject to the verb-pattern gate, see below).
Anything outside = ask.

**Trust zones are configuration, not state.** They don't move during a session. The agent
cannot extend them by issuing commands; only the human (via prompts or config edits) can.

### `WorkingContext.ProjectDirectory` is gone

There is no per-session "project directory" concept anymore. Aaron explicitly killed it.
`set_working_directory` tool is also gone. Any guidance, plumbing, or state related to project_dir
gets removed.

### Three-layer approval gate

**Layer 1 — Hard-deny.** Unchanged. System-protected paths/patterns always block. Operates first.

**Layer 2 — Zone gate.** Parse the command. Extract every directory the command will operate on
(path args, cd targets, redirect targets, output destinations). For each:
- Inside any trusted zone → continue to layer 3
- Outside all trusted zones → prompt the user. Options:
  - **Once** — run this call only, no persistence
  - **Trust this directory** — extend zones for this audience to include `<dir>/*`. Read-only verbs in that tree auto-pass thereafter.
  - **Trust this directory + this verb** — extend zones AND persist a verb-pattern grant for this command shape
  - **Deny**
- Persistence note: "Trust this directory" is a per-audience zone extension, not a per-session one.
  Future sessions on the same audience inherit it.

**Layer 3 — Verb-pattern gate.** Only reached after every path passed Layer 2.
- **Read-only verb** (in the safe-verb list) → silent. The zone gate has already authorized geography;
  the verb is harmless.
- **Mutating verb** (`git push`, `rm`, `sed -i`, ...) → prompt for command-shape approval. Options:
  - **Once** — run this call only
  - **Always for this verb pattern** — persist verb-pattern grant
  - **Deny**
- Persistent verb-pattern grants short-circuit this prompt. Pattern format TBD (see open question 4).

### Read-only verbs **only** auto-pass inside trusted zones

This is a tightening Aaron explicitly called out. Outside-zone paths always prompt regardless of
whether the verb is read-only. The "free pass for read-only" is conditional on the zone gate
having authorized the geography first. Don't ever fall back to "but it's just a `cat`."

### Two persistence stores, not a cross-product

Replace the v2 `(verb, directory)` `ApprovalEntry` shape with two independent persistence stores:

1. **Trusted zones** — per-audience list of directory globs (`/home/user/repos/*`, `/etc/*`, etc.).
   Extended by user clicks on "Trust this directory" or "Trust this directory + this verb." Used
   by the Layer 2 zone gate.
2. **Approved patterns** — per-audience list of verb patterns. Extended by user clicks on
   "Always for this verb pattern" or "Trust this directory + this verb." Used by the Layer 3
   verb-pattern gate.

Each gate is independent. Trusting `/home/user/repos/*` doesn't grant any verb pattern.
Trusting `git push` doesn't grant any directory. The gates compose at evaluation time:
both must pass.

### Project_instructions auto-injection deleted

The daemon currently auto-loads `<project>/.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`,
`CONTEXT.md` based on `WorkingContext.ProjectDirectory` and injects them into the system
prompt. With project_dir gone, this auto-injection goes too.

The agent reads project context **on demand** via `file_read` / `glob`. We update
`Resources/AGENTS.md` to tell the agent: when working on a codebase, look for and read
`AGENTS.md` / `CLAUDE.md` / `.netclaw/AGENTS.md` / `CONTEXT.md` at the project root. Read once,
content lives in conversation history.

This also resolves a token-bloat issue Aaron observed today (`~6k tokens` extra in `in=`
counts when project_dir was set, see PR #940 comment on issue #622 for the broader
instrumentation suggestion).

### cd-in-compound: useful for *parsing*, not for state

The agent's natural idiom is `cd /target && cmd1 && cmd2`. Bash semantics: cmd1 and cmd2 run
in `/target`. The matcher honors this for **extraction within the same compound** — `/target`
counts as a directory the call operates on, plus cmd1 and cmd2 each get attributed to `/target`.

**It does NOT mutate session state.** The agent's cd does not auto-promote anything to a trusted
zone. The next tool call starts fresh — if it has no path arg, the spawn cwd is session_dir
again. The agent has to either re-cd, pass paths explicitly (`git -C /target log`), or accept
session_dir as the spawn cwd.

This is a deliberate tradeoff. We discussed and rejected cd-auto-promote because it's a
security regression (agent extends trust by running a 9-byte command). State stays config-only.

### Multi-path commands resolve naturally

`cp /src/file /dst/file`: zone gate checks BOTH `/src` and `/dst` independently. If both inside
zones, silent geography. If `/dst` is outside, prompt for `/dst` only. After zone-gate passes,
verb gate prompts for cp pattern (mutating verb). Total prompts: at most one per untrusted path,
plus one for mutating-verb pattern. Each prompt is reusable via "always."

No `(cp, single_directory)` cross-product entry to fight with.

### Five-button row — likely shrinks or restructures

The current `(Once, This chat, Always here, Always anywhere, Deny)` row was designed around
the v2 `(verb, directory)` cross-product model. Under the two-gate model the buttons need to
re-think:

- **Layer 2 (zone gate, untrusted-dir prompt):** {Once, Trust this directory, Trust this
  directory + this verb, Deny}
- **Layer 3 (verb-pattern gate, mutating-verb prompt):** {Once, Always for this verb pattern, Deny}

That's two different button rows depending on which gate is firing. Or one unified row that
contextualizes based on what's being asked. UX call.

---

## Tactical findings from OpenCode source (sst/opencode)

Read-only research, not implementation directives. These inform the *how* of our implementation,
not the *what*. The strategic model is settled; these are tactical references.

OpenCode is single-tenant per launch (cwd at launch = project_root) so its model isn't directly
portable, but several specific implementation choices are worth borrowing.

### Their permission/permission directory

`packages/opencode/src/permission/`:
- `index.ts` — service interface, defines `Rule = {permission, pattern, action}`,
  `Reply = once | always | reject`, `Approval = {projectID, patterns[]}`. Three buttons total
  (no This-chat middle ground).
- `evaluate.ts` — five-line wildcard matcher, last-match-wins via `findLast`. Default `ask`.
- `arity.ts` — hand-curated dictionary mapping command prefixes to "how many tokens form the
  verb chain." Flags don't count. `cd: 1`, `git: 2`, `docker compose: 3`, `bun run: 3`, etc.
  Strictly better than our fixed `maxDepth=2 + path-aware-cap`. Worth porting wholesale.

### Their two-gate model

The architectural insight that drove our pivot. `packages/opencode/src/tool/external-directory.ts`
defines `assertExternalDirectory(ctx, target)`. Every file tool calls it before touching a path:

```ts
export const assertExternalDirectoryEffect = Effect.fn(...)(function* (ctx, target, options) {
  if (!target) return
  if (containsPath(full, ins)) return                    // inside project root → silent
  const dir = options?.kind === "directory" ? full : path.dirname(full)
  const glob = path.join(dir, "*")                       // parent-dir wildcard
  yield* ctx.ask({
    permission: "external_directory",
    patterns: [glob],
    always: [glob],
    metadata: { filepath: full, parentDir: dir },
  })
})
```

Their `bash` tool (`tool/shell.ts`) ALSO calls `external_directory` for directories the command
will touch (extracted from the AST), independently of the bash-pattern check. Same call can
produce TWO ask events (directory + bash-pattern), independently persisted.

### Their bash directory extraction (`shell.ts:collect`)

```ts
const CWD = new Set(["cd", "chdir", "popd", "pushd", "push-location", "set-location"])
const FILES = new Set([...CWD, "rm", "cp", "mv", "mkdir", "touch", "chmod", "chown", ...])

for (const node of commands(root)) {
  const tokens = parts(node).map(p => p.text)
  const cmd = tokens[0]?.toLowerCase()
  if (cmd && FILES.has(cmd)) {
    for (const arg of pathArgs(command, ps, shellKind === "cmd")) {
      const resolved = yield* argPath(arg, cwd, ps, shell)
      if (!resolved || containsPath(resolved, instance)) continue
      const dir = (yield* fs.isDir(resolved)) ? resolved : path.dirname(resolved)
      scan.dirs.add(dir)
    }
  }
  if (tokens.length && (!cmd || !CWD.has(cmd))) {
    scan.patterns.add(source(node))
    scan.always.add(BashArity.prefix(tokens).join(" ") + " *")   // glob-style
  }
}
```

Notes for our implementation:

1. **They use tree-sitter-bash AST parsing**, not regex. Handles quotes/escapes/redirects/heredocs
   correctly. Mature library. .NET binding exists. Big upgrade vs our `ShellTokenizer` which is
   regex-based.
2. **Per-verb pathArgs filter** — knows `chmod +X` is special, skips Windows-cmd `/X` flags.
   We have `LooksLikeArgument` which is much blunter. Per-verb table is the right shape.
3. **`argPath` resolution pipeline** — unquote → `~` expansion → env var expansion (`$HOME`,
   `$PWD`, `${env:VAR}`) → strip `filesystem::/path` prefixes → resolve relative against cwd.
4. **Dynamic-path skip** — tokens with unresolved variables or globs they can't expand are
   skipped. We'd extract `~/repos/$VAR` literally today; that's a subtle bug.
5. **`fs.isDir` stat** — actual syscall vs our `Path.HasExtension` heuristic. Theirs is more
   accurate (catches extensionless files like `Makefile`, dot-suffixed dirs like `node_modules.bak`).
   Cost: one syscall per path. Negligible for our latency budget.
6. **CWD verbs (cd) are excluded from `scan.patterns`** but their path arg goes into `scan.dirs`.
   The cd "command" itself doesn't need pattern approval; only the directory it targets needs zone
   approval. Same idea applies to our verb-pattern gate: cd's pattern probably never needs approval
   under the new model.
7. **Pattern storage format**: glob-style `git push *` rather than verb-only `git push`.
   Functionally equivalent for matching but the explicit `*` makes it clear it's a wildcard.
8. **They also add cwd to scan.dirs** if the spawn cwd isn't inside the project. So a bash call
   with no path args from a foreign cwd still produces a directory ask for the cwd. Our equivalent:
   spawn cwd is session_dir which is always trusted, so this case rarely fires for us.

---

## Live evidence from Aaron's dogfood sessions today

### Session ID `D0AC6CKBK5K/1778362405.301519` (the one that motivated session-scratch hide)

Compound: `netclaw doctor --help; echo "---"; netclaw bootstrap --help`. No path arg on any
clause (commands ran from session_dir cwd). User clicked "Always here." Persisted entries:

```json
{ "verb": "netclaw doctor",    "directory": "/home/petabridge/.netclaw/sessions/D0AC6CKBK5K_1778362405_301519" }
{ "verb": "netclaw bootstrap", "directory": "/home/petabridge/.netclaw/sessions/D0AC6CKBK5K_1778362405_301519" }
{ "verb": "which netclaw",     "directory": "/home/petabridge/.netclaw/sessions/D0AC6CKBK5K_1778362405_301519" }
```

These are dead-on-arrival entries. The session_dir won't recur. They illustrate the v2
mismatch between "Always here" semantics and what the user actually wanted.

### Session ID `D0AC6CKBK5K/1778362405.301519` (different prompt, same session)

Compound: `cd /home/petabridge/repositories/stannardlabs/netclaw && git remote -v && echo "---" && git worktree list && echo "---" && git branch -a | grep -E "..."`.

Header read: *"Approve in /home/petabridge/.netclaw/sessions/D0AC6CKBK5K_1778362405_301519?"* with bullets `cd, git remote, echo, git worktree, git branch`. Aaron's reaction: *"It's saying like, oh, do you want to do all this work in this GitHub repository? But the current directories are session directory."*

The user understood the command's effective directory from the first cd target
(`/home/petabridge/repositories/stannardlabs/netclaw`) but the daemon showed session_dir.
Mismatch between "where the call lands" and "where the call is being made from."

### Current state of `~/.netclaw/config/tool-approvals.json` on Aaron's machine

After rebuild + dogfooding, contains a mix of valid path-scoped entries (e.g.
`(find, /home/petabridge)`, `(ls, /home/.../publish)`) and the dead session-dir entries.
**On rewrite, advise wiping the file** since v2 hasn't shipped beyond Aaron's box and the new
storage shape is incompatible (two stores instead of cross-product).

---

## Open design questions for the next session

These need answers before specs/tasks get drafted. Ordered by dependency.

1. **Migration story for the existing `~/.netclaw/config/tool-approvals.json` shape.**
   Probably: wipe and re-prompt. v2 hasn't deployed. New schema is two stores; old is one.
   No migration logic worth writing.

2. **Storage file structure for the two stores.** One file with two top-level sections, two
   files (`tool-approvals.json` for verbs + `trusted-zones.json` for zones), or per-audience
   sub-objects. Backwards-compat consideration: any deployed CLI commands like `netclaw approvals
   trust-verb` need to keep working with the new shape (possibly with rename).

3. **Prompt UX: sequential or batched for two gates?** A call hitting both Layer 2 and Layer 3
   could produce two prompts back-to-back, or one prompt that asks both questions at once. Two
   prompts is cleaner mental model but more clicks; batched is nicer UX but harder to render
   concisely on Slack.

4. **Mutating-verb pattern format.** OpenCode-style globs (`git push *`) or our verb-chain
   (`git push`)? Globs are more expressive (`rm /tmp/*` allowed but `rm /home/*` denied). Verb-
   chain is what the v2 store has today. Consider compatibility with the `netclaw approvals
   trust-verb <verb>` CLI — what does it accept?

5. **TUI for managing trust zones?** Today `netclaw approvals` has list/revoke/trust-verb/TUI
   for the (verb, directory) entries. Under the two-store model the TUI needs to surface both
   axes. New page (`netclaw zones`?) or extend the existing approvals page.

6. **Audience-config exposure for trusted zones.** Today the trust profile defaults are
   per-audience in `netclaw.json`. Operator-extended zones should presumably persist into the
   same structure or a sibling file. Need to decide where they go and how the wizard surfaces
   them.

7. **What replaces `(verb, directory)` in the live `tool-approvals.json` parser.** All the
   existing CLI / TUI / `IToolApprovalMatcher` code reads this shape. Rewrite touchpoints are
   numerous. List of all consumers of `ApprovalEntry` is in the change's design doc; that list
   needs to be updated for the new shape.

8. **Project_instructions file lookup — replace with what?** Currently auto-injected. Under the
   new model the agent reads on demand. We need to update `Resources/AGENTS.md` with explicit
   guidance: which filenames to look for, when to read them, in what order. The candidate list
   currently is `[".netclaw/AGENTS.md", "CLAUDE.md", "AGENTS.md", "CONTEXT.md"]` — keep this
   ordering, just shift the consumer from daemon to agent.

9. **Five-button row replacement.** Layer 2 prompt buttons vs Layer 3 prompt buttons differ.
   Need wireframes / spec for both prompt shapes.

10. **Slack/Discord adapter changes.** Both `SlackApprovalBlockBuilder` and
    `DiscordApprovalPromptBuilder` rendering needs updates. Resolution-line copy (`Saved: ...`)
    needs to handle the new persistence axes.

11. **AST parser adoption: tree-sitter-bash or stay with regex?** Big tactical call. AST is
    correct; regex is what we have. Defer to implementation phase — strategic model doesn't
    care.

12. **Per-verb path-arg rules.** Adopt OpenCode's `FILES`/`CWD` table or build our own.
    Probably copy theirs and add Windows-side equivalents from `CMD_FILES`.

13. **Where does the in-compound cd propagation live?** It's pure parsing — could go in
    `ShellTokenizer` extending the candidate extraction, or in a higher layer that walks the
    candidate list post-extraction. Cleaner in the extractor. No semantic change either way.

---

## What survives from the path-extraction PR work

Even with the rewrite, several committed items on the branch are independently good and should
survive:

- **`01a142e3` cwd fix** (Always here actually persisting cwd) — was fixing a bug that no longer
  exists in the new model (cwd doesn't go in the entry), but the underlying issue (cwd was being
  silently dropped from `ToolInteractionRequest`) was real. The fix touches `ToolApprovalContext`
  shape which gets rewritten anyway. Drop the fix wholesale; it's subsumed.
- **`25e34f7d` path-extraction matcher + side-effect skip** — Verb-only extraction stays useful
  (the verb-pattern gate still wants clean verb chains). Side-effect skip is moot under new model
  (those verbs probably auto-pass via the verb-pattern gate). Path-extraction itself becomes part
  of the layer-2 directory-extraction logic.
- **`7e84da84` agent guidance docs** — most of this gets rewritten. The "Declaring Project Scope"
  section in `Resources/AGENTS.md` needs to switch to "Trust zones are configured by your
  operator; here's how to read project context yourself."
- **`f54c2e68` proposal/design/specs** — most is invalidated by the architectural pivot.
  proposal.md needs a rewrite. design.md needs a rewrite. specs/tool-approval-gates/spec.md
  needs a rewrite. tasks.md needs a rewrite. Basically the whole change directory gets rebuilt.
- **`579a4f6e` side-effect candidates auto-allow at match time** — was fixing a v2.1-specific
  bug (retry-after-Always-anywhere crashed because echo wasn't in the persisted store). Under
  the new model side-effect verbs auto-pass at the verb-pattern gate, so the fix's logic is
  subsumed.

**`fe5c89b3` streaming fix from PR #947** — unrelated to approvals, stays.

---

## Concrete next actions for the next session

1. **`git restore .`** to discard the uncommitted session-scratch + header-location patches.
2. **Read this CONTINUATION.md document.** Verify nothing's missing.
3. **Run through the 13 open design questions with Aaron** and lock answers. Especially #2
   (storage file structure), #3 (sequential vs batched), #4 (pattern format), #5 (TUI shape).
4. **Rewrite `proposal.md`** to reflect the trust-zone architecture.
5. **Rewrite `design.md`** with the three-layer gate, two-store persistence, AST parsing
   adoption decision.
6. **Rewrite `specs/tool-approval-gates/spec.md`** with the new requirements (and probably
   create new requirements for trust-zones since that's a new capability).
7. **Possibly rewrite the change name itself.** `approval-policy-path-extraction` no longer
   describes the scope. Candidates: `approval-policy-trust-zones`, `approval-policy-call-
   inspection`, `approval-policy-rewrite`. Aaron's call.
8. **Rewrite `tasks.md`** against the new design.
9. **Implement.** Probably iterate matcher → persistence stores → CLI/TUI → adapter rendering →
   agent guidance.
10. **Manual binary-swap validation.** Aaron's machine is the test bed.

---

## Things to NOT relitigate

The session covered each of these and they're settled. Don't re-derive.

- **`set_working_directory` is removed.** Don't propose keeping it. Not even as a fallback.
- **`WorkingContext.ProjectDirectory` is removed.** No exceptions.
- **Auto-promoting cd to project_dir was rejected** — security regression. The agent doesn't
  get to extend trust through commands.
- **Cwd does not factor into approval decisions.** It exists only as `psi.WorkingDirectory`
  for the spawned subprocess. It does not appear in any approval matcher logic.
- **Read-only verbs do not auto-pass outside trusted zones.** Outside zones, every verb
  prompts. Read-only-only inside zones.
- **Project_instructions auto-injection is removed.** Agent reads on demand.
- **Trust zones are configuration, not state.** They are extended via prompts (which are user
  decisions) but never via agent action.
- **Two independent gates: zone + verb-pattern.** Not a single (verb, directory) cross-product
  entry. Two separate persistence stores.

If any of these gets relitigated in the next session, point at this document.

---

## Files to review when picking up

- This document (`openspec/changes/approval-policy-path-extraction/CONTINUATION.md`).
- `proposal.md`, `design.md`, `specs/tool-approval-gates/spec.md`, `tasks.md` — to know what
  the existing artifacts say (they'll get rewritten).
- `src/Netclaw.Configuration/ToolAudienceProfileResolver.cs` — to understand how trust profiles
  currently express read-allowed roots.
- `src/Netclaw.Configuration/ToolApprovalStore.cs` — current `(verb, directory)` storage.
- `src/Netclaw.Security/IToolApprovalMatcher.cs` — current matcher interface.
- `src/Netclaw.Actors/Tools/ToolAccessPolicy.cs` — current three-layer gate (hard-deny → safe-verb
  → approval). Layer numbering will change but the layered shape stays.
- `src/Netclaw.Actors/Tools/SetWorkingDirectoryTool.cs` — about to be deleted.
- `src/Netclaw.Configuration/Resources/AGENTS.md` — the section to rewrite.
- `feeds/skills/.system/files/netclaw-operations/SKILL.md` — same.
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs:PersistApprovalCandidatesAsync` — current
  persistence path; gets ripped out and replaced.

---

## Status of the daemon swap on Aaron's machine

Last binary-swap was at the `579a4f6e` commit. Aaron has been dogfooding against that build.
The session-scratch/header-location patches in the working tree are NOT swapped in (they were
never committed or pushed).

`~/.netclaw/config/tool-approvals.json.v1.bak` exists from yesterday's v1→v2 migration test.
`tool-approvals.json` has the dogfood entries described above.

Next swap will require building from whatever the rewrite produces. Don't rebuild before the
rewrite is complete; it'd serve no purpose.

---

## Who's reading this

If you're a fresh Claude session loading this document: read it linearly start-to-finish before
proposing anything. The context is dense but ordered. Aaron is the operator; defer to his
architectural calls; don't re-derive decisions captured in "Things to NOT relitigate."

The work product target is a coherent OpenSpec change in
`openspec/changes/approval-policy-path-extraction/` (or a renamed sibling) that captures the
trust-zones rewrite. Tasks for implementing it. Then drive implementation through `/opsx-apply`
once Aaron approves the artifacts.
