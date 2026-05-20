## Context

Tool access for the three trust audiences is gated by two independent layers:

1. **Tool allowlist** — `ToolAudienceProfileResolver.IsToolAllowed`. A tool is
   gated only if it is in the hard-coded `IsProfileManagedTool` set; otherwise
   it is universally allowed. For gated tools, `ToolsMode = Allowlist` consults
   `ToolAudienceProfile.AllowedTools`; `ToolsMode = All` (Personal only) allows
   everything.
2. **Filesystem scope** — `ScopedFileAccessPolicy` resolves per-audience read /
   write roots from `ReadFiles` / `WriteFiles` / `AttachFiles` plus
   `GlobalReadRoots`. Public is confined to `{session_dir}`; Team and Personal
   additionally get `{workspaces_dir}`, `{skills_dir}`, `{identity_dir}`.

The default profiles (`ToolAudienceProfileDefaults.CreatePublic/Team/Personal`)
are the runtime fallback when config omits a profile, and are what `netclaw init`
scaffolds. Audience is derived per inbound message by `AudienceResult.Resolve`
(shared by Slack and Discord ACL policies), carried in `MessageSource`, and
narrowed by `TrustContextDeriver` into the `EffectiveTrustContext` that tool
authorization reads. None of this is persisted — audience is re-derived each
turn — so the change carries no migration of stored state.

The defects and the agreed fix are described in `proposal.md`.

## Goals / Non-Goals

**Goals:**

- Default profile-managed grants monotonic across `Public ⊆ Team ⊆ Personal`.
- `file_edit`, `web_search`, and `web_fetch` gated by the audience allowlist
  like every other write-class / outbound tool — Public gets none of them.
- Non-Personal audiences can enumerate directories without `shell_execute`.
- `Team` audience reachable only by operator-vetted users / explicit channels.

**Non-Goals:**

- Webhook or MCP access for `Team` (stays operator-opt-in).
- Any change to the `shell_execute` Personal-only hard gate
  (`shell_requires_personal_context`).
- Principal-aware tool gating (tool grants stay keyed on audience, not
  `PrincipalClassification`).
- Recursive directory walks — `file_list` is single-level for this change.

## Decisions

**Symmetric monotonic fix, not document-the-asymmetry.** Public drops to
read/enumerate/attach; Team gains the full file + reminder + skill set. The
trust ladder is restored in code rather than explained away in prose.
Alternative considered: only remove `file_write` from Public (issue's literal
ask) — rejected because it leaves Team unable to write to its own session dir,
a real workflow gap.

**Team keeps `ToolsMode = Allowlist` with an explicit list, not `ToolsMode = All`
minus shell.** `ToolsMode = All` would be terser but (a) `ToolAudienceProfilesDoctorCheck`
errors on `ToolsMode = All` for non-Personal profiles by design, to keep policy
visible, and (b) an explicit list is fail-closed for future profile-managed
tools — a newly added tool stays denied for Team until someone consciously adds
it, rather than silently landing in Team's grants.

**`file_edit`, `file_list`, `web_search`, and `web_fetch` join
`IsProfileManagedTool`.** `file_edit`, `web_search`, and `web_fetch` were
absent — a hidden bypass that exposed them to every audience regardless of the
allowlist. For `web_search`/`web_fetch` that meant a Public (untrusted) session
could drive outbound web requests, including `web_fetch` against arbitrary URLs
(an SSRF / untrusted-content-ingestion surface). Adding them (plus the new
`file_list`) makes the allowlist authoritative for all file and web tools, and
keeps the deployment-wide search feature flag as an orthogonal kill switch.
Consequence: a custom config whose explicit Team/Personal `AllowedTools` omits
one of these will now correctly deny it; this is closing the bypass, not a
regression. Default `Team` and `Personal` retain all of them; `Public` gets
none.

**`file_list` reuses `ScopedFileAccessPolicy`, no new profile field.** The
listing tool resolves its target through the existing read-access path, so it
inherits per-audience root scoping for free: Public lists only `{session_dir}`,
Team also lists workspaces, Personal lists anything. Alternative considered: a
dedicated `ListFiles` filesystem-access profile field — rejected as redundant
with `ReadFiles` + `GlobalReadRoots`.

**DM classification tightened in the shared `AudienceResult.Resolve`.** The
fallback changes from `(isDirectMessage || isExplicitUser || isExplicitChannel)`
to `(isExplicitUser || isExplicitChannel)`. One edit covers Slack and Discord
(both call the shared resolver, as do the two thread-history fetchers). The
`channelAudiences[channelId]` and `channelAudiences["dm"]` explicit overrides
are evaluated first and still win, so operators retain a path to opt all DMs
into `Team`.

**Public `WriteFiles` stays session-scoped.** After the change Public has no
write or edit tool, so its `WriteFiles` scope is unreachable in production. It
is kept at `Roots[{session_dir}]` rather than `None` so that an operator who
deliberately re-grants Public a write tool inherits the safe session-directory
scope instead of an unusable profile — and so the `FileWriteTool` /
`FileEditTool` unit tests that exercise the scoping mechanism through a Public
context stay valid. The authoritative "Public cannot mutate files" statement is
the `AllowedTools` allowlist, not the filesystem scope.

## Risks / Trade-offs

- **Public default-config regression** — operators relying on the default
  Public profile lose `file_write` / `file_edit` and `web_search` / `web_fetch`
  in public channels. → Mitigation: documented as BREAKING; the intent is
  exactly this tightening; explicit `Tools.AudienceProfiles.Public` override
  remains available.
- **DM reclassification surprises operators** — a workflow where a non-vetted
  user DMs the bot and expected Team-level tools now gets Public. → Mitigation:
  `ChannelAudiences.dm = "team"` restores the old behavior explicitly;
  documented in configuration.md and the `netclaw-operations` skill.
- **`file_edit` / `web_search` / `web_fetch` gating affects custom configs** —
  an explicit allowlist that omitted one of them silently allowed it before and
  denies it now. → Mitigation: this is the correct fail-closed behavior; called
  out in release notes / spec delta. Default `Team`/`Personal` retain all three.
- **New enumeration surface on Public** — `file_list` lets a Public session
  enumerate its session dir. → Mitigation: read-only, session-scoped, with
  sanitized denied-path errors (no root-path leakage), mirroring
  `PublicAudienceFileAccessPolicyTests`.

**Failure modes:** a Public session invoking `file_write`/`file_edit` fails
loud with `tool_not_allowed_for_audience_profile` (no silent degradation);
`file_list` outside allowed roots returns a sanitized denial; a non-allowlisted
DM resolves to Public and any Team-only tool call from it denies loudly. No
actor-lifecycle or persistence impact — audience and profiles are recomputed
per turn from unchanged `MessageSource` data, so resumed sessions and persisted
reminders re-derive the new behavior automatically on the next turn.

## Migration Plan

No data migration. Backward-compatible at the config layer: configs that
explicitly define `Tools.AudienceProfiles` are untouched (each audience profile
falls back independently via `GetResolvedProfile`). Deployments on default
profiles adopt the new grants and DM classification on upgrade. Rollback is a
plain revert; `netclaw doctor` validation is unaffected (it checks profile mode
shape, not `AllowedTools` contents).
