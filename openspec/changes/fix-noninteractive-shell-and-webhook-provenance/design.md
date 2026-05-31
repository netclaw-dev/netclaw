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
- Confine autonomous (non-interactive) sessions to a filesystem zone so the
  unified `Mode.All` path authorization cannot be combined with the safe-verb
  auto-approval to read arbitrary out-of-zone files unattended.

**Non-Goals:**

- OS-level shell sandboxing. The autonomous zone is path-argument confinement, not
  process isolation; real OS confinement is `ShellExecutionMode.SandboxOnly` + a
  backend, which this change does not build or rely on.
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
- **Autonomous filesystem clamp (the human-backstop substitute).** Unifying shell
  with the file-access policy means an unrestricted (`Mode.All`) Personal audience
  authorizes any path. Combined with the pre-existing safe-verb short-circuit
  (`ScopedShellSafeVerbPolicy.AllShortCircuit`), which auto-approves a read-only verb
  based on the *cwd* and never inspects the command's *path arguments*, an
  unattended session processing a hostile payload (a webhook) could be steered to
  auto-read arbitrary out-of-zone files (`cat ~/.ssh/id_rsa`). The fix is to confine
  autonomous (`SupportsInteractiveApproval == false`) sessions to a filesystem zone:
  - *Axis is interactivity, not audience.* The clamp keys on the absence of a human
    approval backstop, which is exactly `SupportsInteractiveApproval`. It is the
    substitute for the human a non-interactive channel cannot summon.
  - *Single seam.* Because shell now routes through `TryResolveWritePath` and every
    file tool through `TryResolvePath`, the clamp lives at one place —
    `ScopedFileAccessPolicy.TryResolvePath`'s `Mode.All` short-circuit — and covers
    all filesystem tools at once. Confining only shell would move the vector to
    `file_read`.
  - *Narrows, never widens.* The zone replaces the `Mode.All` "allow-all" allowance
    and otherwise intersects with the audience's roots, so a more-restricted audience
    (e.g. autonomous Public, session-scoped) is never loosened.
  - *Derived from data already on the context.* The write/attach zone is
    `session_dir` + `project_dir`, both already on `ToolExecutionContext`; reads also
    reach the existing `_cachedGlobalReadRoots` (skills/identity/workspaces). This
    reuses what already flows through the seam rather than inventing a config knob or
    threading `NetclawPaths` through tool constructors, and is the corrected version
    of the original trust-zone bug — which sourced roots from the audience profile's
    `WriteFiles.Mode` (`All` → empty for Personal).
  - *Alternative — taint-gating:* confine only turns carrying `PayloadTaint` from
    external input, leaving operator-authored unattended work unrestricted. More
    precise, but `PayloadTaint` is not currently threaded onto `ToolExecutionContext`
    (only `TurnContext`/`SourceProvenance`), so it needs new plumbing. Deferred —
    the folder zone uses fields already on the context and is simpler/predictable.
  - *Alternative — narrow the safe-verb list for autonomous:* rejected. For a
    non-interactive channel there is no prompt to fall back to, so removing a verb
    deletes the capability outright, and the only recovery (`trust-verb cat`) grants
    that verb *everywhere* — broader than the hole being closed. Restricting folders
    instead preserves capability and makes the recovery a bounded "add a root".

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
  controls. Note the heuristic is robust *in combination*: a command simple enough
  to auto-run unattended is simple enough for path extraction to see its paths, and
  a command complex enough to hide its paths (control flow, `python -c`, command
  substitution) is flagged `messy` and fails closed for non-interactive callers.
- **[Autonomous zone too tight bricks legitimate work; too loose re-leaks]** →
  Reads reach the global read roots (incl. workspaces), so an autonomous session can
  read across the project tree for triage; writes are confined to `session_dir` +
  the current `project_dir`. A reminder/webhook scoped to a project (project_dir set)
  can write there; one with no project writes only to its session scratch — the safe
  floor. Widening unattended writes beyond the current project is deliberately not a
  default (it would be the exfil/abuse surface).
- **[Autonomous clamp partially reverses the shipped `Mode.All`→anywhere shell
  behavior]** → Intended. For non-interactive contexts, path authority comes from
  the channel zone rather than the audience's `Mode.All`; the unification still holds
  for interactive sessions and for the single resolution seam.

## Migration Plan

No data migration. Existing persisted webhook routes keep their stored audience.
The change is backward compatible at the config level (no `*Config` shape change).
Rollback is a straight revert.

## Open Questions

- None blocking. The exact webhook registration boundary that should host the
  escalation guard (tool vs. manager/registration actor) is an implementation
  detail resolved during coding by mirroring the reminder minting path.
