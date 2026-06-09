## Context

This change introduces `netclaw config` as the main post-install settings
surface. The IA is now locked as domain-oriented and heavier on sub-pages,
not a flat menu of every registered leaf editor.

The section-editor abstraction remains the implementation substrate, but the
config command is free to group leaves, route some entries into existing
commands, and keep some capabilities out of scope entirely.

## Goals / Non-Goals

**Goals:**

- Ship `netclaw config` as the main post-install settings command.
- Use a domain-oriented root dashboard.
- Keep Security Posture, Enabled Features, Audience Profiles, and Exposure
  Mode distinct under `Security & Access`.
- Keep the existing `Daemon` config shape and global shell-mode shape.
- Route MCP permissions editing out to `netclaw mcp permissions` instead of
  duplicating it.
- Use generalized save validation across all leaf editors.

**Non-Goals:**

- Editing Identity here.
- Adding MCP Servers to this branch.
- Flattening the IA to match registry order.
- Refactoring command back-stack behavior.
- Adding new persisted exposure-mode fields outside the current config
  shape.

## Decisions

### D1. Root IA is domain-oriented

The dashboard root is a navigation page with domain entries, not a flat
list of all leaf editors. The root contains:

- Inference Providers
- Models
- Channels
- Inbound Webhooks
- Skill Sources
- Search
- Browser Automation
- Telemetry & Alerting
- Security & Access

Alternative considered: render every registered editor directly in one flat
screen. Rejected because the locked IA is intentionally domain-oriented and
heavier on sub-pages.

### D2. Routed handoffs are valid top-level outcomes

`Inference Providers` routes to `netclaw provider` and `Models` routes to
`netclaw model`. This branch accepts the handoff without redesigning
navigation history.

Alternative considered: re-host provider and model editors inline. Rejected
for scope and because the user explicitly accepted routed handoffs here.

### D3. Security posture, enabled features, and audience profiles are separate

These concepts are explicitly decoupled:

- Security Posture sets the deployment stance.
- Enabled Features controls deployment-wide runtime enablement.
- Audience Profiles is a curated high-level per-audience editor.

For Team and Public posture flows, changing posture continues into Enabled
Features. Personal skips that continuation.

Alternative considered: keep per-audience feature toggles inside Audience
Profiles. Rejected because runtime enablement is deployment-wide, not a
per-audience policy surface.

### D4. Audience Profiles is curated, not raw config

Audience Profiles edits only:

- Tool Access (non-MCP)
- File Access
- Incoming Attachments
- Reset to posture default

It does not expose per-audience runtime feature toggles, per-audience shell
mode, MCP grants, or approval-policy editing. Reset/overwrite resets the
full underlying profile, including hidden MCP/approval settings.

### D5. MCP permissions route out to the existing command

If an operator needs MCP access, grant, or approval editing, the config
surface directs them to `netclaw mcp permissions`.

### D6. Exposure Mode keeps the existing `Daemon` shape

Exposure Mode uses explicit modes:

- Local
- Reverse Proxy
- Tailscale Serve
- Tailscale Funnel
- Cloudflare Tunnel

`Daemon.ExposureMode` is the single active selector. Mode-specific dialogs
edit only fields already supported by the current config shape. Inactive
values remain preserved.

Alternative considered: one collapsed Tailscale option or new per-mode
active flags. Rejected by the locked decisions.

### D7. First non-local enablement may bootstrap pairing automatically

If the operator enables a non-local exposure mode and no bootstrap/pairing
state exists, the config flow auto-pairs the current configuring client. If
bootstrap state exists but is orphaned or mismatched, the flow blocks and
points the operator to `netclaw doctor`, the docs, and issue `#875`.

### D8. Validation is generalized, not one bug-specific rule

Every leaf editor validates what it edits before save: paths, URIs,
credentials, binary presence, referenced entities, and remote resource
reachability where appropriate. Structural invalidity is a hard block;
runtime/probe failures can present `Save anyway`.

This closes the planning gap around `#1151` by making validation a general
leaf-editor rule rather than a one-off search bug workaround.

### D9. Missing install refuses before any TUI starts

If no install/config exists, `netclaw config` prints a plain non-zero
message directing the operator to `netclaw init`. No partial dashboard or
placeholder shell renders.

### D10. Coverage follows ownership

Leaf editors get substantive round-trip and smoke coverage. Routed handoffs
get shallow routing coverage only.

### D11. Inline config editors autosave completed actions through one shared contract

Inline config editors use a shared autosave interaction component instead of
page-specific save buttons or one-off status text. The standard behavior is:

- `Esc` backs out or cancels incomplete input; it never saves.
- Completed actions save immediately after validation.
- Text and multi-field input becomes a completed action only when accepted
  with `Enter` / Apply.
- Toggles, audience changes, enable/disable, add/remove, and confirmed reset
  actions are completed actions.
- Structural validation failures block writes and leave disk unchanged.
- Runtime/probe failures may offer `Save anyway` only after the structurally
  valid draft is known.
- Each write is section-preserving and field-scoped to the editor's ownership
  boundary.

Alternative considered: explicit `[s] Save` staged editing. Rejected because
the existing config surfaces behave like action editors, and mixing staged
edits with navigation caused operators to lose unrelated channel configuration.
The safer user model is “doing the thing saves the thing,” with `Esc` reserved
for navigation/cancel.

## Risks / Trade-offs

- The domain-oriented IA introduces more navigation depth.
  Mitigation: the structure matches operator mental models and keeps the
  root from becoming an unscannable flat list.
- Routed handoffs create command-context boundaries.
  Mitigation: accepted for this branch; avoid stack refactors here.
- Audience Profiles hides some underlying settings.
  Mitigation: that is intentional; reset/overwrite semantics explicitly
  restore the full underlying profile, including hidden settings.
- Exposure-mode auto-pairing can fail on inconsistent state.
  Mitigation: fail loudly and route to doctor/docs/#875 rather than doing
  inline repair.
- Autosave can surprise operators if every keypress writes.
  Mitigation: only completed actions autosave; incomplete text entry remains
  an in-memory draft until accepted with `Enter` / Apply.

## Migration Plan

1. Land `netclaw config` as the primary post-install settings command.
2. Keep provider/model/MCP permission power-user commands in place.
3. Keep Identity in init.
4. Preserve existing config shape during migration; no `Daemon` section
   rearrangement is required.

## Open Questions

None. The locked decisions remove the earlier IA ambiguity.
