## Context

The interactive approval gate has a layer-2 short-circuit
(`ScopedShellSafeVerbPolicy.AllShortCircuit`): when *every* candidate verb chain
in a shell command is on a bundled safe-verb list AND the command's cwd resolves
inside a trusted zone, the command auto-approves with no prompt. The bundled
lists (`safe-verbs.linux.json`, `safe-verbs.windows.json`) currently cover file
readers (`cat`, `ls`, `grep`, `find`, …) and read-only git subcommands
(`git status`, `git log`, …) but omit common trivially read-only verbs — most
visibly `date`, which generated the originating spam report.

Two facts constrain the implementation:

1. The safe-verb list is matched by **exact equality** against the verb chain
   produced by `ShellApprovalSemantics.ExtractVerbChain`, which is **greedy** —
   it extends through every non-flag, non-path, non-URL token. So `date
   +%Y-%m-%d` would extract `date` only because `+%Y-%m-%d` is treated as an
   argument; `ps aux` extracts `ps aux`; `which ilspycmd` extracts `which
   ilspycmd`. `ShellTokenizer.ApplyVerbShortCircuit` already caps a curated set
   (`PathAwareVerbs`, `SingleTokenSideEffectVerbs`) at depth 1 so `grep secret`
   → `grep`.
2. The matcher itself is sound and out of scope — see proposal.

## Goals / Non-Goals

**Goals:**

- Stop interactive prompts for demonstrably read-only verbs invoked in a
  trusted zone.
- Keep the security boundary intact: trusted-zone gating and the
  all-clauses-safe conjunction are unchanged.
- Make the bundled list and the verb-chain extractor agree, so a listed verb
  actually matches its realistic invocations.
- Correct the stale `tool-approval-gates` verb-chain scenario so the spec
  matches the intentional greedy extractor.

**Non-Goals:**

- No change to `tool-approvals.json`, `AddApproval`, grant supersession, or
  matcher logic.
- No change to verb-chain granularity (greedy extraction stays — `git fetch
  origin dev` remains distinct from `git fetch`).
- No session-scoped ("This chat") persistence change.
- Not resolving the pre-existing spec-vs-bundle discrepancy over whether
  `~/.netclaw/config/safe-verbs.<os>.json` overrides are read at runtime — out
  of scope; this change only widens the shipped defaults.

## Decisions

**D1 — Widen the bundled JSON lists, not a code-level set.** The safe verbs
already live in `safe-verbs.{linux,windows}.json` as the single source of truth;
new verbs go there. Each added verb must pass a stated bar: cannot write/delete
files, cannot execute arbitrary code, cannot POST/PATCH/DELETE to a network
endpoint. Excluded for that reason: `git tag` (`git tag <name>` creates a tag),
`git fetch` (mutates the local object store), `gh api` (arbitrary HTTP method),
`curl`, `dotnet`, and command-prefixing verbs (`env`, `xargs`, `sudo`,
`timeout`, `nohup`) — depth-1 capping a command-prefixing verb would let
`env rm -rf ~` resolve to the verb `env` and auto-pass. Alternative considered:
a broad "any read-only-looking verb"
heuristic — rejected; the curated allow-list with an explicit bar is auditable
and matches the constitution's safe-verb-list review rule.

**D2 — Extend the depth-1 cap for new single-token verbs.** For the safe-verb
match to fire, a verb's realistic invocation must extract to exactly the listed
chain. Single-token system verbs that commonly carry a bare-word operand
(`date`, `ps`, `which`, `uname`, `uptime`, `free`, `id`, `hostname`,
`whoami`, `groups`, `printenv`, `nproc`) are added to the depth-1 cap in
`ShellTokenizer` via a new `SingleTokenCommandVerbs` set (a sibling of
`PathAwareVerbs` — these verbs do not consume a path so reusing the
"path-aware" set would be a misnomer). `env` is deliberately NOT capped or
listed: it can prefix an arbitrary command. Multi-token `git`/`gh` read verbs are **not** depth-capped:
`gh pr list`, `gh pr view 1234` (numeric operand dropped), and `gh pr list
--state open` (flag stops the chain) match cleanly; `gh pr view <bare-branch>`
extracts a longer chain and still prompts — acceptable partial coverage,
consistent with the intentional greedy extractor. Alternative considered:
rewrite the extractor to be operand-aware per CLI — rejected as a much larger
change the team explicitly decided against.

**D3 — Correct the spec scenario rather than the extractor.** The
`tool-approval-gates` scenario asserting `git push origin main` → `git push` is
stale; greedy extraction (`git push origin main` → `git push origin main`) is
intentional. The spec is corrected to match the code, not vice versa
(constitution: fix planning artifacts to reflect reality).

## Risks / Trade-offs

- [A widened auto-pass surface could let a borderline verb through] → Mitigation:
  the explicit cannot-mutate/exec/exfiltrate bar; the trusted-zone gate is
  unchanged; the all-clauses-safe conjunction means a safe verb chained with any
  non-safe verb still prompts; layer-1 hard-deny is untouched.
- [A new verb is listed but its common form doesn't extract to the bare chain,
  so it silently still prompts] → Mitigation: D2 plus a per-verb extraction test
  asserting realistic invocations (`date +%Y-%m-%d`, `ps aux`, `uname -a`)
  resolve to the listed verb.
- [Depth-1 capping a verb hides a meaningful sub-verb] → Low: the capped verbs
  are single-token commands with no sub-command grammar (`date`, `ps`, …),
  unlike `git`/`gh`.

## Migration Plan

Pure additive data + extractor change; no persistence or schema migration. The
bundled lists ship with the next daemon build. Rollback is reverting the JSON
and `ShellTokenizer` change. Existing `tool-approvals.json` grants are unaffected.

## Open Questions

None blocking. The final exact verb list is reviewed at implementation against
the D1 bar.
