## Context

`add-tool-authorization-spec` added the `tool-authorization` capability. This
change removes the duplicate normative text. It is the first change in this
repository that retires main specs (`retire_capabilities: true`).

## Goals / Non-Goals

**Goals:**

- One owner for each tool authorization rule.
- A written reason for each removed requirement.
- No loss of a current, true rule.

**Non-Goals:**

- Change code or behavior.
- Clean up specs outside the approval domain, except the two nearby items in
  decision D6 (fold `skill-trust-tiers`, and archive the in-flight approval
  changes).

## Decisions

1. **Move before retire.** Ingress rules that are not tool authorization keep
   their text and name in their new specs. A reviewer can compare them one to
   one.
2. **Enrich before remove.** Where a removed requirement holds a true detail
   that TA-n lacks, this change adds that detail to TA-n in the same change.
3. **Keep lifecycle rules in their specs.** `session-state-machine`,
   `session-resume` "Graceful stop of durable approval waits" and "Idle
   passivation proceeds with pending approvals", and the channel capability
   flags in `netclaw-input-adapters` describe actor or channel lifecycle, not
   authorization. They stay.
4. **Keep accurate tool contracts.** `file_list`, `file_read`, and
   `attach_file` keep their tool contracts in `netclaw-tools`. Their
   authorization sentences agree with TA-6.
5. **Case detail lives in fixtures.** Removed requirements for Bash causal
   intent and stdin data point to the case catalog and the evidence fixtures,
   per decision D6.

## Risks / Trade-offs

- [A later archive of an open change can target a retired capability] → The
  in-flight approval changes were archived first. `stop-repeated-tool-cycles`
  and `subagent-explicit-model-selection` stay open; they touch only
  requirements that this change keeps.
- [The `.system` collision rule is true only after PR #2249] → Merge this
  change after #2249.
- [Guidance rules from `tool-approval-gates` have no new home] → Listed as an
  owner decision in the PR.

## Migration Plan

Docs only. Revert the PR to restore the retired specs.
