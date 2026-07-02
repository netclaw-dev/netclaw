## Context

Approval prompts for tool calls flow from `ToolAccessPolicy` (a domain service that decides whether a tool invocation requires a human approval) through `LlmSessionActor` (which raises a `ToolInteractionRequest`) into channel-specific builders — `SlackApprovalBlockBuilder` and `DiscordApprovalPromptBuilder`. The policy layer is intentionally channel-agnostic: it produces a list of `ToolApprovalOption` records (key + label) and a list of `DirectoryRoots`, and channel adapters render them however their platform allows.

PR #896 (directory-scoped shell approvals) added a UX touch in the channel-agnostic policy layer that does *not* belong there: it overrode the `ApprovalOptionKeys.ApproveSession`/`ApproveAlways` labels to interpolate the directory `DisplayPath`. Slack's `PlainText` button text is hard-capped at 76 characters, Discord's button label at 80 — neither limit was enforced or known to the policy layer. For typical project paths the resulting label is 100-130 chars, Slack rejects with `invalid_blocks`, the channel post fails, and `SendApprovalDenyOnFailureAsync` auto-denies the request as a safety net. The user sees a "couldn't post the approval prompt — automatically denied" error for a tool call they never had a chance to approve.

## Goals / Non-Goals

**Goals:**

- Eliminate the silent auto-deny path triggered by oversized button text on Slack.
- Apply the same correctness fix to Discord (which has the same exposure with a slightly higher 80-char cap).
- Keep directory-scope context visible to the user so approvals remain informed.
- Preserve the underlying directory-root approval behavior introduced by #896 — only the *label rendering* changes; storage, matching, and CLI surfaces are untouched.

**Non-Goals:**

- Adding defensive truncation in channel builders. With fixed labels sourced from compile-time constants, there is no runtime input that can exceed the platform caps; truncation would be belt-and-suspenders for a problem that no longer exists at the source.
- Changing the message-body rendering of directory roots. Both Slack (`*Directory Roots*` section block) and Discord (`**Directory Root:**` summary line) already render the path in body text where the platforms have generous limits (Slack section block: 3000 chars; Discord message: 2000 chars). Long paths wrap naturally.
- Restructuring the policy/channel boundary further. The right principle is "policy emits semantics, channels handle presentation," but a fuller cleanup is out of scope for this fix.

## Decisions

### 1. Drop the dynamic label override, do not truncate

**Decision:** Delete the `if (directoryRoots.Count == 1) { sessionLabel = $"Approve shell access in {root.DisplayPath} ..."; ... }` block in `ToolAccessPolicy.CheckApprovalGate` and use `ApprovalOptionKeys.ApproveSessionLabel`/`ApproveAlwaysLabel` unconditionally.

**Rationale:**

- The directory path is already shown in the message body by both channel builders. The dynamic label was duplicating that information into a constrained surface.
- Fixed labels sourced from compile-time constants (≤21 chars) are structurally incapable of breaching either platform's button cap, eliminating the failure mode at the source.
- Channel-agnostic policy should not encode channel-specific length budgets.

**Alternatives considered:**

- **Truncate-with-ellipsis in channel builders.** Rejected — keeps the smell that policy emits presentation strings, hides the path on long inputs (worst case the user sees `"Approve shell access in /home/.../in… for this chat"` which is *less* readable than the fixed verb), and adds testing surface area for a defensive guard that fixed labels make unnecessary.
- **Move the dynamic label construction to channel builders.** Rejected for this change — would preserve the dynamic label on Discord (where 80 chars is still tight for absolute paths) and would just relocate the same overflow risk. If we later want channel-specific verbosity, that's a separate design.
- **Keep the dynamic label but pass `ComparisonRoot` instead of `DisplayPath`.** Rejected — `ComparisonRoot` is the *normalized* path which is generally *longer*, not shorter, and still routinely exceeds 76 chars.

### 2. Document the platform caps at the button-construction sites

**Decision:** Add a single-line comment at the Slack `Button { Text = new PlainText(option.Label) }` block and the Discord `DiscordButtonSpec(... Label: option.Label ...)` construction noting the platform's hard cap and pointing to `ApprovalOptionKeys` as the source of safe defaults.

**Rationale:** The bug recurs whenever someone reintroduces dynamic labels without knowing the cap exists. A code comment at the enforcement site is the cheapest, most discoverable warning. CLAUDE.md's comment guidance explicitly favors comments that document hidden constraints; platform button-text caps are exactly that.

### 3. Spec inversion, not deletion

**Decision:** Replace the existing `Dynamic approval option labels` requirement at `tool-approval-gates/spec.md:447-470` with a new requirement that mandates fixed verb-only labels.

**Rationale:** The spec must be authoritative. Leaving the old requirement in place — even archived — would let a reader believe Netclaw mandates dynamic labels. Inverting the requirement is the same delta-spec mechanic OpenSpec was designed for.

## Risks / Trade-offs

- **[Risk]** A user on a multi-root approval glances at the buttons and isn't sure which root they're approving for. **Mitigation:** Both channels render the full root list in the message body immediately above the buttons. The policy layer also already collapses to "Approve in these directories ..." for >1 root, which had its own ambiguity; that case goes away too with fixed labels.
- **[Risk]** A future contributor sees the fixed labels and decides to reintroduce per-tool customization, recreating the bug. **Mitigation:** The Slack/Discord builder comments document the platform cap. The new spec requirement is the policy-level guardrail.
- **[Trade-off]** Users lose the at-a-glance reminder of which directory they're approving in the button text itself. We accept this — the body section is the source of truth, and approvals shown without a body are a different bug.

## Migration Plan

No runtime migration. Persisted approvals key off `ApprovalEntries` (the directory roots), not button labels — existing approvals are unaffected. The change is in-place and atomic with the next daemon release.

**Rollback:** Revert the `ToolAccessPolicy` edit and the spec change. No data shape changes mean rollback is purely a code revert.

## Open Questions

None.
