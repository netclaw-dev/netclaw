## Context

See `proposal.md` for the motivation and `specs/tool-authorization/spec.md`
for the rule. The approval matcher lives in `Netclaw.Security`. The store
hygiene lives in `Netclaw.Configuration`, which cannot reference
`Netclaw.Security`. Before this change, each project had its own word
comparison.

## Goals / Non-Goals

**Goals:**

- One word rule for the matcher, the save-time skip, and the doctor.
- No new reach for a link word: the path checks see its target.

**Non-Goals:**

- Any change to the save rule, the prompt, or the stored format.
- Command-specific knowledge of any kind.

## Decisions

- **One shared rule in `Netclaw.Configuration`.**
  `ToolApprovalEntryComparer.CoversCommandWords(grantWords, candidateWords, shell)`
  holds the rule. `ApprovalPatternMatching` calls it for both match kinds, and
  `ApprovalGrantHygiene.Covers` calls it for grant-to-grant coverage. The
  comparer already owns the case rules for both callers. Alternative: a copy in
  each project. Rejected: two copies drift.

  ```text
  schematic:
  covers(W, C) = |W| >= 1
                 and |C| >= |W|
                 and (|W| >= 2 or |C| == 1)
                 and W[i] == C[i] for each i < |W|
  ```

  The same function answers "does grant W cover grant N": if `|W| >= 2` and N
  starts with W, each call that N covers starts with W. If `|W| == 1`, then N
  must equal W. So a doctor removal never changes an allowed decision.

- **A link word adds its link path as a path scope.** Before this change, a
  link word stayed a command word, so only a grant that named it covered the
  call. A verb grant now covers later words without a name, so the link must
  get the path checks. The scope is the link path itself, not its folder, so
  the trusted-root and protected-path checks resolve the target. Alternative:
  make a link word unresolved (exact consent only). Rejected: it makes a
  harmless link stricter than its `./link` spelling and changes the prompt
  options.

- **Legacy grants use the same rule.** A `LegacyExact` phrase splits into its
  words. The kind name stays for the stored format only.

Data ownership: the rule is a pure function (call-local). Grants stay durable
in `ToolApprovalStore`; chat grants stay actor-local in `ToolApprovalActor`.
No actor message or persisted record changes.

## Risks / Trade-offs

- [A verb grant covers more calls, for example `git push --force origin main`
  under a `git push` grant] → The owner accepted this reach. Hard deny, path
  checks, audience, and scope still apply. A narrower saved grant stays narrow.
- [A stored link-word grant in a folder now meets the folder link check] →
  The call prompts again, which fails closed.
- [`netclaw doctor --fix` removes more grants] → It removes only a grant whose
  calls another grant still covers. The invariant test checks this with the
  real matcher.

## Migration Plan

No data migration. Rollback: an older binary reads the same store and uses the
exact rule again, so grants never get wider after a rollback. A grant that the
doctor removed stays removed; the next prompt saves it again.
