## Why

Netclaw re-prompts for interactive approval of trivially read-only shell verbs
(`date`, `whoami`, `uname`, read-only `git`/`gh` queries) even when they cannot
mutate state, delete files, execute arbitrary code, or POST to a network
endpoint. This is the dominant, fixable source of approval-prompt spam reported
from production sessions (e.g. `date` re-prompted in session
`D0AC6CKBK5K/1779109308.394429`). The approval matcher itself is sound — global
grants already supersede folder-scoped grants — so the lever is the bundled
safe-verb auto-allow list, not the matcher.

## What Changes

- Expand the bundled safe-verb auto-allow lists (`safe-verbs.linux.json`,
  `safe-verbs.windows.json`) with demonstrably read-only verbs: system/info
  verbs (`date`, `whoami`, `id`, `uname`, `ps`, …) and read-only `git`/`gh`
  query subcommands (`git describe`, `gh pr view`, `gh run list`, …). The
  trusted-zone gate and the all-clauses-must-be-safe conjunction are unchanged —
  the security boundary holds.
- Add a depth-1 verb-chain cap for the new single-token command verbs so
  realistic invocations (`date +%Y-%m-%d`, `ps aux`, `which ilspycmd`) extract
  to the bare verb and match the list. This also fixes the pre-existing partial
  breakage of `which` (already listed but currently extracting with its operand).
- Correct a stale scenario in the `tool-approval-gates` spec: verb-chain
  extraction is greedy, so `git push origin main` extracts to `git push origin
  main`, not `git push`. The scenario currently asserts the wrong result.
- Add an explicit precedence statement to the spec: a global
  (`directory: null`) grant authorizes a verb in every directory and supersedes
  a coexisting folder-scoped grant, which is retained for revocation
  reversibility.

Not in scope: no changes to `tool-approvals.json`, `AddApproval`, grant
supersession-on-write, or the matcher logic; verb-chain granularity stays
greedy by design; session-scoped ("This chat") approval persistence is a
separate concern.

## Capabilities

### New Capabilities

<!-- None. This change widens bundled data and corrects existing requirements. -->

### Modified Capabilities

- `tool-approval-gates`: the "Shell command pattern matching" requirement is
  corrected — its verb-chain extraction scenario asserted `git push origin main`
  → `git push`, but greedy extraction keeps operands (`git push origin main`); a
  new "Global grant precedence over folder-scoped grants" requirement codifies
  that a global grant authorizes a verb everywhere while coexisting folder-scoped
  grants are retained (not superseded) for revocation reversibility. The
  safe-verb list widening is a bundled-data change and does not alter the
  "Safe-verb auto-allow short-circuit" requirement's normative behavior.

## Impact

- Source: `src/Netclaw.Configuration/SafeVerbs/safe-verbs.linux.json`,
  `safe-verbs.windows.json`; `src/Netclaw.Security/ShellTokenizer.cs` (depth-1
  verb-chain cap set).
- Verify-only (no change expected): `src/Netclaw.Configuration/SafeVerbList.cs`,
  `src/Netclaw.Actors/Tools/ScopedShellSafeVerbPolicy.cs`.
- Tests: `SafeVerbLoaderTests`, `ScopedShellSafeVerbPolicyTests`, `ShellTokenizer`
  verb-chain extraction tests.
- Docs / agent guidance: `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  (version bump per the System Skills Sync Rule), `docs/runbooks/tool-approval-gates.md`.
- Security posture (PRD-002, Gateway Security Envelope): widens the layer-2
  interactive-approval auto-pass surface only; the layer-1 hard-deny list and
  the trusted-zone requirement are untouched. Every added verb is justified
  against a cannot-mutate / cannot-exec / cannot-exfiltrate bar.
- No API, persistence-schema, or configuration-schema changes.
