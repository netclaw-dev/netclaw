## Why

PR #896 introduced directory-scoped shell approvals and, as a UX touch, made the approval button labels dynamic — embedding the directory `DisplayPath` into option labels (e.g. `"Approve shell access in /home/petabridge/repositories/petabridge/testlab-setup/services/kubernetes/ingress/ for this chat"`). For realistic project paths these labels exceed Slack's hard 76-character button-text limit, Slack rejects the message with `invalid_blocks`, and `SendApprovalDenyOnFailureAsync` auto-denies the request as a safety net. The user sees a "couldn't post the approval prompt" error and a silent self-deny for a tool call they never had a chance to approve. Discord has the same exposure (80-char button cap). The directory-scope context the dynamic label was carrying is already conveyed in the message body via the existing `*Directory Roots*` Slack section block and Discord summary line, so the dynamic label adds no information — only failure modes.

## What Changes

- **BREAKING (UX)**: Approval option labels for shell tool approvals SHALL be fixed verb-only strings sourced from `ApprovalOptionKeys` (`Approve once`, `Approve for this chat`, `Approve always`, `Deny`), independent of the tool, command, or directory root being approved.
- Remove the dynamic label override in `ToolAccessPolicy.CheckApprovalGate` that constructs `"Approve shell access in {root.DisplayPath} ..."` style labels; remove the now-dead `IsRelativeDisplayPath` helper.
- Add comments at the Slack and Discord button-construction sites documenting the platform button-text caps (Slack 76 chars, Discord 80 chars) so future contributors don't reintroduce dynamic labels without understanding the constraint.
- Directory scope continues to be conveyed to the user via the message body (`SlackApprovalBlockBuilder` Directory Roots section block, `DiscordApprovalPromptBuilder` Directory Root summary) — no behavioral change there.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `tool-approval-gates`: Inverts the existing **Dynamic approval option labels** requirement (`openspec/specs/tool-approval-gates/spec.md:447-470`). The new requirement mandates fixed verb-only labels regardless of directory roots, with directory-scope context carried by the message body, not the buttons.

## Impact

- **Code**: `src/Netclaw.Actors/Tools/ToolAccessPolicy.cs` (delete dynamic label block + dead helper). One-line comments added in `src/Netclaw.Channels.Slack/SlackApprovalBlockBuilder.cs` and `src/Netclaw.Channels.Discord/DiscordApprovalPromptBuilder.cs` documenting platform button-text caps.
- **Tests**: Three test files flip from asserting dynamic labels to asserting `ApprovalOptionKeys` constants — `ToolApprovalGateTests`, `ParentSessionApprovalBridgeTests`, `DiscordApprovalPromptBuilderTests`. One new long-path regression test in `ToolApprovalGateTests` to pin the Slack-rejection scenario.
- **Persistence / CLI / audit log**: No impact. Button labels are not persisted; `ToolApprovalStore`, `ApprovalsCommand`, and audit logs all key off `ApprovalEntries` (the directory roots themselves), not the user-facing button text.
- **Channel adapters**: No structural change to Slack or Discord rendering — both already include directory-scope context in the message body. Buttons just stop including it as well.
- **Security**: Net-positive. The current bug causes silent auto-denies on long paths, which is fail-closed but trains users to retry blindly. Fixed labels eliminate the failure mode without weakening any approval gate.
