## Why

Three bugs in `approval-policy-v2` (shipped on the same PR, not yet
deployed) prevent folder-scoped trust from compounding across shell
calls. Together they leave the operator approving every shell call and
filling the store with entries that never match again. Surfaced in a
single dogfood session (`D0AC6CKBK5K/1778303523.861279`) where
`tool-approvals.json` ended up with:

```
{ "verb": "find /home/petabridge" }
{ "verb": "find /home/petabridge/.netclaw" }
{ "verb": "ls /home/petabridge/.netclaw/bin/" }
{ "verb": "echo systemctl not available" }
…
```

The bugs:

1. **Verb extraction wrongly includes path arguments.** The v2 design
   pairs `(verb, directory)` so verbs are reusable across paths. The
   extractor instead emits `find /home/petabridge` as the verb,
   collapsing both halves into one string. The next call (`find
   /home/petabridge/.netclaw -name X`) is a different string → no
   match → re-prompt. Folder-scoped trust never compounds because each
   call's path produces a new entry.
2. **The directory half is unused at extract time.** Path arguments to
   shell commands are exactly the directory the gate should be reasoning
   about. The extractor throws that information away, then we fall back
   to cwd — which is null in most cases because the model doesn't call
   `set_working_directory` preemptively. Net effect: the directory
   half of `(verb, directory)` is almost always null even when the
   command literally names the directory.
3. **Compound-command splitting persists pure side-effects.** A
   multi-clause command (`A; B; C` or `A || B`) splits into one
   persisted entry per clause — including side-effect clauses like
   `echo "==="` and `echo "no .bash_profile"` that have no path and no
   future-matching value. Persistence is supposed to mean "I want to
   run this kind of thing again"; recording every literal echo as a
   global wildcard is noise.

## What Changes

This change reframes the verb half of `(verb, directory)` to be the
command head only and uses path arguments as an implicit directory
declaration. Persistence shape stays compatible with v2 — only the
extractor and matcher change. The five-button prompt and quarantine
behavior are unchanged.

- **Verb-only extraction.** `ExtractCandidateVerbs` returns the command
  head plus subcommand chain (`find`, `git status`, `npm install`) —
  *not* the path arguments. Path-looking tokens (starting with `/`,
  `~`, `./`, `../`) are stripped from the verb string and surfaced
  separately as the candidate's effective directory.
- **First-path-wins for multi-arg commands.** `cp /src/a /dst/b`
  extracts `/src/a` as the effective directory (source wins). `git -C
  /repo log` falls back to cwd because the path is hidden behind a
  flag — acceptable, the model can still get the safe-verb short-circuit
  by calling `set_working_directory` explicitly.
- **Effective directory drives matching.** `ApprovalPatternMatching`
  evaluates candidates against persisted entries using:
  `verb == entry.verb AND (entry.directory == null OR
  effectiveDirectory under entry.directory)`. The effective directory
  is the extracted path arg if present, else the cwd. Symlink-segment
  guard still applies along the resolved path.
- **Always here persists with the extracted path.** Clicking
  `approve_always` on `find /home/petabridge` stores
  `(verb="find", directory="/home/petabridge")` — covering future
  `find /home/petabridge/.netclaw -name X` calls automatically. The
  shallow-cwd guard (`IsCwdTooShallow`) extends to extracted paths so
  the button is omitted when the path is too shallow to safely scope
  (e.g. `find /`).
- **Drop side-effect-only verbs from persistence.** When the user clicks
  `approve_always` on a multi-clause command, only clauses with an
  extractable path are persisted. Pure side-effect clauses (`echo X`,
  `printf X`, `true`, `false`) get the approval **for this call** but
  do not pollute the store. The `Once` decision still authorizes them
  exactly once.
- **Persistence shape unchanged.** Stored entries continue to be
  `{verb, directory?}`. Existing entries from the dogfood window
  (e.g. `find /home/petabridge`) won't match new candidates because
  the verb extractor no longer emits path-embedded verbs — they
  become inert and get superseded as the operator approves the new
  forms via prompts. No quarantine, no migration logic, no version
  bump. v2 hasn't shipped to anyone but Aaron; the few stale entries
  on his machine can be deleted by hand or simply allowed to age out.

## Capabilities

### New Capabilities

None. This is a refinement of an existing capability.

### Modified Capabilities

- `tool-approval-gates`: rewrites the verb-extraction and matcher
  contract; adds the implicit-directory rule for `approve_always`
  persistence; adds the "drop pure-side-effect verbs from persistence"
  rule; adds the quarantine path for path-embedded v2 entries.

## Impact

**Source code:**

- `src/Netclaw.Security/ApprovalPatternMatching.cs` — verb extraction
  loses path args; matcher consumes effective-directory.
- `src/Netclaw.Security/IToolApprovalMatcher.cs` — `ExtractCandidateVerbs`
  signature gains a parallel `ExtractCandidateDirectories` (or returns a
  list of `(verb, directory?)` pairs — design choice).
- `src/Netclaw.Security/ShellTokenizer.cs` — path-token classification
  on the way out.
- `src/Netclaw.Actors/Tools/ToolAccessPolicy.cs` — pass effective
  directories through to the matcher and into `ToolApprovalContext`.
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` — persistence path on
  `ApprovedAlways` writes `(verb, extractedPath ?? cwd)` per clause and
  skips pure-side-effect clauses entirely.
- `src/Netclaw.Configuration/ToolApprovalStore.cs` — quarantine for
  path-embedded v2 entries; reuses the v1 `.bak` pattern.

**Channel adapters:**

- `SlackApprovalBlockBuilder` / `DiscordApprovalPromptBuilder` — header
  and resolution line use the effective directory rather than just cwd.
  No new buttons, no new fields.

**Persistence:**

- `tool-approvals.json` shape unchanged. Existing path-embedded v2
  entries quarantine to `tool-approvals.json.embedded.bak` on first
  read.

**Agent guidance:**

- `feeds/skills/.system/files/netclaw-operations/SKILL.md` — bump
  version, update Approval Prompts section to explain that path args
  declare scope automatically; `set_working_directory` becomes useful
  primarily for sessions where commands won't carry an explicit path
  (e.g. interactive REPL work).
- `Resources/AGENTS.md` — soften the "Declare Your Project Root Early"
  imperative; for verb-with-path commands, the act of running the
  command IS the declaration.

**Specs:**

- `openspec/specs/tool-approval-gates/spec.md` — modify the verb
  extraction, matcher, and persistence requirements; add the
  side-effect-verb skip rule; add the embedded-v2 quarantine scenario.
  No changes to `session-cwd` or `netclaw-cli`.

**Security and operational impact:**

- **No security regression.** The new matcher is strictly more precise
  about scope — `(find, /repo)` does not auto-allow `find /unrelated`.
  Symlink-segment guard, hard-deny list, and audience trust profile
  all run unchanged ahead of the new matcher.
- **Quarantine is operator-visible.** The CLI surfaces the new
  `.embedded.bak` quarantine in the same one-line note as v1's
  `.v1.bak` — operators can inspect and re-grant via the prompt.
- **Shallow-path guard extended.** Operators can no longer accidentally
  store `(find, /)` or `(rm, ~)` because the depth check now applies
  to extracted paths too.

**PRD references:**

- `docs/prd/approvals.md` (the v2 PRD) — friction-reduction goals are
  load-bearing for this change. No new PRD section needed; this
  proposal lives as a v2 refinement.
