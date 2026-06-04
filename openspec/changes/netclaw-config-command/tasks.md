## 1. OpenSpec planning artifacts and traceability

- [x] 1.1 Confirm proposal, design, and spec deltas reflect the
  domain-oriented config IA and the locked ownership split.
- [x] 1.2 Remove planning language that still assumes Enterprise posture,
  per-audience runtime feature toggles, per-audience shell mode, inline
  MCP permission editing, flat dashboards, or byte-identical assertions.
- [x] 1.3 Run `openspec validate netclaw-config-command --type change`.

## 2. Command entry and refusal behavior

- [x] 2.1 Add `netclaw config` to CLI routing.
- [x] 2.2 Refuse with a plain non-zero message when no install/config is
  present: direct operators to `netclaw init` and render no TUI.
- [x] 2.3 Keep `--help` discoverable from `netclaw --help`.

## 3. Root dashboard IA

- [x] 3.1 Implement the root dashboard as domain navigation, not a flat
  list of every leaf editor.
- [x] 3.2 Add these root entries: Inference Providers, Models, Channels,
  Inbound Webhooks, Skill Sources, Search, Browser Automation,
  Telemetry & Alerting, Security & Access.
- [x] 3.3 Add Quit and Run Full Doctor affordances at the root.

## 4. Routed handoffs

- [x] 4.1 Route `Inference Providers` to `netclaw provider`.
- [x] 4.2 Route `Models` to `netclaw model`.
- [x] 4.3 Add shallow routing coverage for both handoffs.

## 5. Channels area

- [ ] 5.1 Add `Channels` sub-page containing Slack, Discord, Mattermost.
- [ ] 5.2 Keep each channel editor as a leaf with substantive validation
  and round-trip coverage.

## 6. Skill Sources area

- [x] 6.1 Add `Skill Sources` sub-page containing External Skills and
  Skill Feeds.
- [x] 6.2 Keep validation for paths, URIs, auth, and reachability aligned
  to the generalized save-validation rule.

## 7. Telemetry & Alerting area

- [x] 7.1 Add `Telemetry & Alerting` sub-page.
- [x] 7.2 Include Telemetry and Outbound Webhooks only in this pass.
- [x] 7.3 Defer delivery-policy tuning.

## 8. Security & Access area

- [x] 8.1 Add `Security & Access` sub-page.
- [x] 8.2 Include Security Posture, Enabled Features, Audience Profiles,
  and Exposure Mode.
- [x] 8.3 Keep posture values to `Personal`, `Team`, and `Public` only.

## 9. Security Posture leaf

- [x] 9.1 Keep Security Posture distinct from Enabled Features and
  Audience Profiles.
- [x] 9.2 When posture changes to Team or Public, continue into Enabled
  Features.
- [x] 9.3 When posture changes to Personal, skip the Enabled Features
  continuation.
- [x] 9.4 Support overwrite/reset behavior that resets the full underlying
  audience profile when requested.

## 10. Enabled Features leaf

- [x] 10.1 Implement Enabled Features as deployment-wide runtime
  enablement.
- [x] 10.2 Do not represent Enabled Features as per-audience policy.
- [x] 10.3 Cover runtime-enablement editing with substantive round-trip and
  smoke tests.

## 11. Audience Profiles leaf

- [x] 11.1 Implement Audience Profiles as a curated high-level editor.
- [x] 11.2 Remove per-audience feature toggles from this editor.
- [x] 11.3 Remove per-audience shell mode from this editor.
- [x] 11.4 Limit editable concerns to Tool Access (non-MCP), File Access,
  Incoming Attachments, and Reset to posture default.
- [x] 11.5 Ensure reset/overwrite resets the full underlying audience
  profile, including hidden MCP and approval settings.
- [x] 11.6 Route MCP access/grants/approval editing to
  `netclaw mcp permissions` instead of recreating it here.

## 12. Exposure Mode leaf

- [x] 12.1 Implement explicit modes: Local, Reverse Proxy,
  Tailscale Serve, Tailscale Funnel, Cloudflare Tunnel.
- [x] 12.2 Keep a single active selector via `Daemon.ExposureMode`.
- [x] 12.3 Do not add per-mode active flags.
- [x] 12.4 Keep the existing `Daemon` config shape; do not rearrange
  config sections.
- [x] 12.5 Preserve inactive old values and ignore them when inactive.
- [x] 12.6 Give each non-local mode its own dialog; Local requires no
  extra setup.
- [x] 12.7 Do not add new persisted exposure-specific fields that do not
  exist in the current config shape.
- [x] 12.8 On first non-local enablement, auto-pair the current
  configuring client when no bootstrap/pairing state exists.
- [x] 12.9 If bootstrap state is orphaned or mismatched, block and point
  the operator to `netclaw doctor`, formal docs, and issue `#875`.

## 13. Validation model

- [x] 13.1 Apply generalized pre-save validation to every leaf editor.
- [x] 13.2 Validate paths, URIs, auth, binary presence, local references,
  and remote reachability where relevant.
- [x] 13.3 Keep structurally invalid config as a hard block.
- [x] 13.4 Allow `Save anyway` only for runtime/probe failures.
- [x] 13.5 Update planning/tests around `#1151` so validation is framed as
  a cross-editor rule, not just a narrow search regression.

## 14. Coverage

- [ ] 14.1 Add shared autosave contract tests for every inline config leaf:
  completed actions persist, `Esc` does not save incomplete drafts, and
  invalid completed actions write nothing.
- [ ] 14.2 Add substantive round-trip tests for leaf editors.
- [ ] 14.3 Add substantive smoke tapes for leaf editors.
- [ ] 14.4 Use semantic preservation assertions, not byte-identical file
  assertions.
- [ ] 14.5 Add shallow routing coverage for routed handoffs only.

## 16. Shared autosave config interaction

- [x] 16.1 Introduce a shared autosave interaction component/contract for
  inline config editors.
- [x] 16.2 Remove explicit save-key behavior and copy from inline config
  editors; completed actions autosave instead.
- [x] 16.3 Ensure `Esc` only navigates/cancels and never persists edits.
- [x] 16.4 Ensure each autosave validates before writing and leaves files
  unchanged on validation failure.
- [x] 16.5 Ensure writes are section-preserving and field-scoped to editor
  ownership boundaries.
- [x] 16.6 Harden Channels persistence so provider enable/disable, add/remove,
  audience, allowed-user, direct-message, and credential actions autosave
  provider-granular changes without wiping unrelated providers.
- [x] 16.7 Add the regression: seed Slack and Discord, add a Discord channel,
  disable Slack, press `Esc`, and verify only completed autosaves occurred
  with Slack dormant setup preserved.

## 15. Quality gates

- [x] 15.1 `dotnet build` clean.
- [x] 15.2 `dotnet test` clean.
- [x] 15.3 `./scripts/smoke/run-smoke.sh light` clean.
- [x] 15.4 `dotnet slopwatch analyze` clean.
- [x] 15.5 `./scripts/Add-FileHeaders.ps1 -Verify` clean.
- [x] 15.6 `openspec validate netclaw-config-command --type change`
  passes.
