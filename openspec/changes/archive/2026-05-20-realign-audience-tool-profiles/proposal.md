## Why

The default audience tool profiles run the trust ladder backwards: `Public`
— the least-trusted, fail-closed audience — is granted `file_write` while the
more-trusted `Team` audience is not (issue #1084). Investigation found three
further gaps: `file_edit` bypasses the audience allowlist entirely (it is not
in the profile-managed tool set, so every audience can call it), no tool can
enumerate a directory without `shell_execute` (Personal-only), and any DM —
including one from an unknown external user — is classified as `Team`. Together
these violate the default-deny, fail-closed posture in PRD-002 and make the
documented security model incoherent.

## What Changes

- Default profile-managed tool grants become monotonic across the trust ladder
  `Public ⊆ Team ⊆ Personal`.
- **BREAKING** (default-config behavior): `Public` default `AllowedTools`
  becomes `[file_read, file_list, attach_file]` — read, enumerate, and attach
  only. It loses `file_write`, `file_edit`, `web_search`, and `web_fetch`.
- `Team` default `AllowedTools` is widened to every useful profile-managed tool
  except shell, webhooks, and MCP: `file_read`, `file_list`, `file_write`,
  `file_edit`, `attach_file`, `web_search`, `web_fetch`, `skill_manage`, the
  four reminder tools, and `set_working_directory`.
- `file_edit`, `web_search`, and `web_fetch` join the profile-managed tool set
  so they are audience-gated like `file_write`, closing hidden allowlist
  bypasses. `web_search`/`web_fetch` were previously available to every
  audience including Public — a Public (untrusted) session could drive
  outbound web requests, including `web_fetch` against arbitrary URLs.
- New read-only `file_list` directory-enumeration tool, policy-gated and scoped
  to each audience's existing filesystem read roots, so non-Personal audiences
  can discover files without `shell_execute`. Shell stays Personal-only.
- **BREAKING** (default-config behavior): a DM from a user not on the channel
  allow-list resolves to the `Public` audience instead of `Team`; only
  operator-vetted users and explicit channels resolve to `Team`. Explicit
  `ChannelAudiences` overrides (including `dm`) still take precedence.
- Webhooks and MCP server access remain Personal / operator-opt-in.

In scope for MVP: the changes above and their tests, spec, schema, and
system-skill updates. Out of scope: granting `Team` webhook or MCP access,
and any change to the `shell_execute` Personal-only hard gate.

## Capabilities

### New Capabilities

<!-- None. `file_list` is a new tool within the existing netclaw-tools capability, not a new capability. -->

### Modified Capabilities

- `netclaw-acl`: default audience tool-profile grants become monotonic across
  `Public ⊆ Team ⊆ Personal`; `file_edit`, `web_search`, and `web_fetch` become
  profile-managed, audience-gated tools rather than universally allowed.
- `netclaw-tools`: adds the `file_list` directory-enumeration tool as a
  policy-gated first-party tool.
- `netclaw-input-adapters`: inbound DM trust-context derivation resolves a
  non-allowlisted sender to `Public`, not `Team`.

## Impact

- **Code**: `Netclaw.Configuration/ToolAudienceProfiles.cs` (default profiles),
  `Netclaw.Actors/Tools/ToolAudienceProfileResolver.cs` (profile-managed set),
  new `Netclaw.Actors/Tools/FileListTool.cs` + `ToolRegistrationExtensions.cs`,
  `Netclaw.Channels/AudienceResult.cs` (DM classification — propagates to Slack
  and Discord via the shared resolver).
- **Config / schema**: `docs/spec/configuration.md` audience-profile example;
  verify `netclaw-config.v1.schema.json` `AllowedTools` accepts `file_list`.
- **Onboarding**: `netclaw init` scaffolds profiles via the changed factory, so
  generated configs pick up the new defaults; no wizard code change.
- **System skills**: `netclaw-operations` SKILL.md (set_working_directory
  availability, new `file_list` tool, DM-classification note).
- **Security impact**: tightens the least-trusted `Public` audience to no
  file-mutation and no outbound web tools, closes the `file_edit` /
  `web_search` / `web_fetch` allowlist bypasses, and prevents an untrusted
  external DM from reaching `Team`-level grants. Net reduction in default
  attack surface — notably, a Public session can no longer drive `web_fetch`
  against arbitrary URLs.
- **Operational impact**: configs that explicitly define `Tools.AudienceProfiles`
  are unaffected (each profile falls back independently). Configs relying on
  defaults gain the monotonic grants. Operators who intentionally want all DMs
  treated as `Team` set `ChannelAudiences.dm = "team"`.
- **Eval suite**: tool-grant change — run `./evals/run-evals.sh` and add a
  `file_list` discovery/use case.
