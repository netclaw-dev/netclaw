## Context

The non-interactive shell "trust zone" (`ToolAccessPolicy.EnforceShellTrustZones`,
`IShellTrustZonePolicy`/`ShellTrustZonePolicy`) was added to sandbox shell path
arguments for channels that cannot prompt for approval. It resolves a set of
allowed roots via `ScopedFileAccessPolicy.GetRootsForContext(..., Write)` and denies
any path outside them.

Two facts make it always fail for the only audience that reaches it:

1. Shell is Personal-only — `ToolAccessPolicy.cs:135` denies
   `shell_requires_personal_context` for any non-Personal audience, so the
   trust-zone block downstream is only ever evaluated with audience == Personal.
2. The Personal profile's `WriteFiles.Mode == All`. `GetRootsForContext` → 
   `ToolAudienceProfileResolver.ResolveRoots` returns `[]` for any non-`Roots` mode,
   and the trust-zone treats an empty roots list as deny-all
   (`shell_no_trust_zone_roots`).

The same class interprets `Mode == All` the opposite way for file tools:
`ScopedFileAccessPolicy.TryResolvePath` short-circuits `Mode == All` to allow-all
(`ScopedFileAccessPolicy.cs:64-68`). So `file_write` to a path succeeds while
`shell` touching the same path is denied — the sandbox blocks the honest path and
leaves the `file_write` path open.

Separately, webhook audience does not follow the established
channel → session → {sub-agent, reminder} provenance model:
`SetWebhookTool` never reads `context.Audience` and hard-defaults to `Public`, with
no escalation guard. Reminders implement the intended model
(`SetReminderTool.cs:227` inherit, `ReminderManagerActor.ValidateRequestedAudience`
downgrade-only guard).

## Goals / Non-Goals

**Goals:**

- Remove the `shell_no_trust_zone_roots` false-denial so pre-approved verbs run in
  non-interactive channels (issue #1244).
- Collapse the two divergent interpretations of `Mode.All`/`Roots`/`None` into one
  shared resolution for shell and file tools.
- Bring webhook route creation into the existing audience-provenance model
  (inherit creator audience, downgrade-only escalation guard).

**Non-Goals:**

- Sandboxing shell execution. Real OS-level confinement is `ShellExecutionMode`
  `SandboxOnly` + a backend; this change does not build or rely on it.
- Enabling shell for non-Personal audiences (the Personal-only gate is unchanged).
- Deleting the trust-zone mechanism, changing the hard-deny list, `ToolPathPolicy`
  (protected paths), per-audience file confinement, or the approval gate.

## Decisions

- **Unify, don't delete.** Fix the root divergence by making the non-interactive
  shell path check reuse `ScopedFileAccessPolicy.TryResolveWritePath` per path token
  and for the working directory, instead of the hand-rolled
  `GetTrustZoneRoots` + `IsWithinAnyRoot`. One code path then governs `Mode.All`
  (allow), `Mode.Roots` (confine), and `Mode.None` (deny), plus the existing symlink
  defense — for both shell and file tools.
  - *Alternative — delete the mechanism:* removes the false-deny but discards
    coherent path-confinement scaffolding for a future `Mode.Roots` shell audience.
    Rejected because unifying is the true root-cause fix (eliminates the divergence
    rather than amputating one side) at comparable footprint.
  - *Alternative — only reorder the gate (issue Option B):* leaves the `Mode.All`
    ↔ empty-roots contradiction in place. Rejected.
- **Contract change on `IShellTrustZonePolicy`.** Replace `GetTrustZoneRoots(context)`
  (roots listing) with a write-path authorization method delegating to the wrapped
  `ScopedFileAccessPolicy.TryResolveWritePath`. Token extraction and
  working-directory normalization stay in `ToolAccessPolicy`; only the per-path
  decision is delegated. The existing `_shellTrustZonePolicy is null` fail-closed
  branch is unchanged.
- **Webhook provenance mirrors reminders.** `SetWebhookTool` overrides the
  context-aware `ExecuteAsync`, resolves audience as `requested ?? context.Audience`,
  and the webhook registration boundary validates against escalation
  (downgrade-only) the same way `ReminderManagerActor.ValidateRequestedAudience`
  does. Config-defined routes keep `Public` as the fail-closed default.

## Risks / Trade-offs

- **[Personal non-interactive shell is now governed solely by approvals]** → This is
  intended: the approval gate fails closed for non-interactive callers unless the
  verb is pre-approved/safe-listed, and protected paths (`ToolPathPolicy`) plus the
  hard-deny list still apply. Net posture is unchanged or tighter.
- **[Webhook provenance changes the default audience for agent-created routes from
  Public to inherited]** → A route created from a Team session becomes Team (was
  Public); a Personal session's route becomes Personal. The escalation guard
  prevents minting above the creator's authority, and execution uses the stored,
  validated audience. Documented in `netclaw-operations`.
- **[Path-token validation is a heuristic, not a sandbox]** → Unchanged from today;
  explicitly a non-goal. The approval gate and protected-path policy are the real
  controls.

## Migration Plan

No data migration. Existing persisted webhook routes keep their stored audience.
The change is backward compatible at the config level (no `*Config` shape change).
Rollback is a straight revert.

## Open Questions

- None blocking. The exact webhook registration boundary that should host the
  escalation guard (tool vs. manager/registration actor) is an implementation
  detail resolved during coding by mirroring the reminder minting path.
