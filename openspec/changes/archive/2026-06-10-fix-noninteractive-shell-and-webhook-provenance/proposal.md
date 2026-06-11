## Why

Non-interactive channels (`Headless`, `Reminder`, `Webhook` — where
`SupportsInteractiveApproval == false`) fail **every** shell command with
`shell_no_trust_zone_roots`, even for pre-approved verbs, making unattended shell
execution impossible (issue #1244). The root cause is a semantic divergence inside
`ScopedFileAccessPolicy`: file tools read `WriteFiles.Mode == All` as *allow-all*,
while the non-interactive shell "trust zone" reads the same `Mode == All` as
*deny-all* (it resolves to an empty roots list and denies). Because shell is
Personal-only and Personal is `Mode == All`, the trust-zone gate is only ever
reached with `Mode == All` and therefore always denies.

A second, related gap surfaced while diagnosing this: webhook audience is **not**
inherited from the creator the way reminder and sub-agent audiences are. Webhook
route creation hard-defaults to `Public` and has no escalation guard, breaking the
established channel → session → {sub-agent, reminder} provenance model.

## What Changes

- Non-interactive shell path validation is **unified** with the file-write access
  policy: shell path arguments and the working directory are validated through the
  same audience-scoped resolution (`Mode.All` ⇒ unrestricted, `Mode.Roots` ⇒
  confined to roots, `Mode.None` ⇒ denied) that `file_write`/`file_edit` already
  use. This removes the divergent empty-roots logic and the unconditional
  `shell_no_trust_zone_roots` denial for the Personal audience. Pre-approval and the
  approval gate remain the authoritative authorization for unattended shell.
- Webhook route creation (`set_webhook`) **inherits the creating context's
  audience** by default instead of defaulting to `Public`, and validates the
  requested audience against escalation (downgrade-only), mirroring reminder minting
  validation. File-defined (hand-edited config) webhook routes keep `Public` as the
  fail-closed default since no creator context exists.
- **Autonomous (non-interactive) channels are confined to a filesystem zone**
  across every filesystem-touching tool, even under the Personal audience. Because
  unifying shell with the file-access policy makes an unrestricted (`Mode.All`)
  Personal audience authorize any path, an unattended session that processes a
  hostile payload (e.g. a webhook) could otherwise be steered to read arbitrary
  out-of-zone files via a read-only "safe verb" (whose auto-approval inspects the
  verb and cwd, not the path argument). The zone is derived from data already on the
  execution context — the session directory and current project directory for
  write/attach; reads additionally reach the existing non-sensitive global read roots
  (skills, identity, workspaces) — so an autonomous session reads across the project
  tree but writes only within its session or current project. It substitutes for the
  human approval backstop autonomous channels lack, narrows only (never widens a
  scoped audience), and applies at a single seam in `ScopedFileAccessPolicy` covering
  shell and the file tools together. No new config and no new plumbing — both the
  context fields and the cached read roots already exist. **BREAKING:** none —
  defaults preserve current interactive behavior; autonomous sessions move from
  "unrestricted under Personal" to "zone-confined".
- No change to the hard-deny list, protected-path policy (`ToolPathPolicy`), the
  Personal-only shell gate, the approval gate, or interactive filesystem behavior.

## Capabilities

### New Capabilities

<!-- None — this change refines existing capabilities. -->

### Modified Capabilities

- `tool-approval-gates`: Add a requirement that for non-interactive channels, shell
  path arguments and working directory are authorized through the same
  audience-scoped file-access resolution as write-capable file tools, so a single
  interpretation of `Mode.All`/`Roots`/`None` governs both. The Personal audience
  (`Mode.All`) is authorized (subject to the approval gate); a `Mode.Roots` audience
  confines paths to its roots; the prior unconditional empty-roots denial is removed.
- `inbound-webhooks`: Webhook route creation SHALL inherit the creating context's
  audience by default and SHALL reject a requested audience that exceeds the
  creator's authority (downgrade-only), aligning webhook minting with the reminder
  minting/validation requirement in `netclaw-scheduling`. Execution continues to use
  the route's stored, validated audience.
- `audience-context-filtering`: Autonomous (non-interactive) channels SHALL be
  confined to a non-empty filesystem zone (session + project + configured roots)
  across all filesystem tools, regardless of audience; the clamp narrows only and
  leaves interactive sessions governed by audience + configuration.

## Impact

- **Code:** `src/Netclaw.Actors/Tools/ToolAccessPolicy.cs`
  (`EnforceShellTrustZones`), `src/Netclaw.Actors/Tools/ShellTrustZonePolicy.cs` +
  `src/Netclaw.Tools.Abstractions/IShellTrustZonePolicy.cs` (contract changes from
  roots-listing to write-path authorization, delegating to `ScopedFileAccessPolicy`),
  `src/Netclaw.Actors/Tools/SetWebhookTool.cs` (context-aware audience inheritance),
  the webhook registration boundary that persists `RegisteredWebhookRoute`
  (escalation guard), and tests in `src/Netclaw.Actors.Tests/Tools/`.
- **Autonomous clamp:** a single seam in
  `src/Netclaw.Actors/Tools/ScopedFileAccessPolicy.cs` (`TryResolvePath` — the
  `Mode.All` short-circuit) applies the zone for `SupportsInteractiveApproval == false`
  contexts; zone roots come from `ToolExecutionContext.SessionDirectory` /
  `ProjectDirectory` (+ the existing `_cachedGlobalReadRoots` for reads). No new
  config, schema, constructors, or DI threading.
- **Security:** Net posture is unchanged or tightened. Config/secrets/keys/webhooks
  remain protected by `ToolPathPolicy` (audience-independent, applies to shell and
  file tools). Per-audience file confinement is unchanged. Webhook provenance closes
  a forced-downgrade gap and adds an escalation guard. Unattended shell remains gated
  by pre-approval and the approval gate (which fails closed and re-routes the model
  for non-interactive callers).
- **Operational:** The issue repro starts working — a pre-approved verb
  (`netclaw approvals trust-verb`) runs in `netclaw chat -p` / reminders / webhooks.
  `set_webhook` description changes (audience now inherited when omitted); the
  `netclaw-operations` system skill is updated accordingly.
- **PRD/source:** No new PRD; traces to issue #1244 and the existing
  `tool-approval-gates`, `inbound-webhooks`, and `netclaw-scheduling` specs.
