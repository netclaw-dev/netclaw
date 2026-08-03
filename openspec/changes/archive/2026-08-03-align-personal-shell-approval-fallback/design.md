## Context

The Personal init path writes `ShellMode = HostAllowed` and an explicit `shell_execute = Approval` override. `ToolAccessPolicy` also selects `Approval` when a Personal shell call has no exact override.

`ToolAudienceProfilesDoctorCheck` instead treats a missing policy as `Auto`. The main specification also says that runtime defaults do not place shell in `Approval` mode.

The daemon actor boundary and approval persistence do not change. The change only aligns diagnostics and the contract with the existing authorization decision.

## Goals / Non-Goals

**Goals:**

- Make the doctor report the effective Personal shell mode.
- State the clean-install and fail-closed fallback rules.
- Preserve explicit `Auto`, `Approval`, and `Deny` shell overrides.
- Add focused regression proof for doctor output.

**Non-Goals:**

- Change shell authorization or approval persistence.
- Add the approval disposition matrix.
- Change shell parsing or PR #1733.
- Change Team or Public shell access.

## Decisions

### Use the exact shell override as the doctor signal

The doctor will warn only when the Personal profile explicitly sets `shell_execute` to `Auto`. A missing policy or missing exact override resolves to `Approval` in the runtime.

This approach uses `ToolApprovalConfig.TryGetExplicitMode`. It does not add a second general approval resolver.

Alternative: move the runtime resolver into a new shared service. This change rejects that option because the contract correction needs no new runtime abstraction.

### Keep the clean install explicit

The init wizard will continue to write `shell_execute = Approval`. Operators can inspect the normal security posture without knowledge of the runtime backstop.

The fallback remains necessary for old or partial configuration. It prevents a missing field from enabling host shell without approval.

### Keep actor and persistence behavior unchanged

The session actor, approval actor, and `tool-approvals.json` format do not change. Existing approvals remain valid.

## Risks / Trade-offs

- **Risk:** A future runtime change could diverge from the doctor again. **Mitigation:** Tests cover missing policy, missing override, and explicit override cases.
- **Risk:** Operators can misread a missing warning as an explicit configuration endorsement. **Mitigation:** The specification distinguishes the generated override from the runtime backstop.
- **Risk:** A doctor message change can affect documentation or scripts. **Mitigation:** The new message remains a warning and names the explicit `Auto` override.

## Migration Plan

No configuration migration is required. Existing Personal configurations retain their current runtime behavior.

Rollback restores the old diagnostic and specification text. It does not change stored approvals or daemon state.

## Open Questions

None.
