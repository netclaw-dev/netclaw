<!--
  Copyright (C) 2026 Petabridge, LLC
-->
# PR 8 proactive-persistence migration checkpoint

## Purpose and status

This is a checkpoint for the PR 8 proactive-reminder architecture correction.
The merged-dev architecture gate and the complete live matrix passed. The
channel-root translation corrections are merged. The channel ACL identity is
reconciled without a change to the exact default-deny policy. The reminder
policy gate is also reconciled. An unmapped Teams channel stays `Public`. The
approved live team and channel use an explicit `Team` mapping in protected
owner-only configuration. The final upload smoke rejected the activity before
actor, model, Graph, download, staging, or reply work. PR 8 task 8.3 is complete.

- Validated branch: `dev`
- Validated head: `fb468474`
- Pre-checkpoint head: `4baf137c`
- Safety reference: `backup/teams-pr8-before-quality-review`

## Why the rework was required

Teams persistence had leaked into generic Actors. A direct move would make
existing journals and snapshots unreadable. The prior design also had a
permanent-invalidation crash gap, no destination generation, one mutable
delivery correlation, no known-destination resolver, non-authoritative
diagnostics, and no request-context disposal proof.

## Implemented architecture

New Teams records, snapshots, protobuf schema, and serializer are Teams-owned.
The generic serializer reads legacy Teams manifests into a decode-only envelope;
the Teams recovery actors convert that envelope and write only v2 snapshots.
Destination generation is retained per delivery. A permanent delivery failure
records its terminal state and matching-generation invalidation in one v2 event.
Session output carries immutable per-delivery correlation. The daemon no longer
caches an SDK request context. The binding actor resolves its own current or
explicit canonical destination. It fails closed for unavailable and
cross-session destinations. Actor-owned diagnostics expose safe bounded state.

## Responsibility map

- Generic compatibility: `Netclaw.Actors` legacy envelope, legacy protobuf
  definitions, and decode-only manifests.
- Teams active state: `Netclaw.Channels.Teams` persistence contracts, serializer,
  recovery conversion, binding state, channel index, and resolver.
- Daemon boundary: Teams SDK send uses the app-level sender only.
- Tests: Actors serialization and Teams routing/persistence coverage.

## Legacy migration table

| Legacy manifest/type | Legacy payload shape | Decode owner | Teams v2 write path | Conversion and trigger | Unsupported/lossy behavior |
| --- | --- | --- | --- | --- | --- |
| `tapc-v1`, `tacd-v1`, `taco-v1` | approval pending, card locator, terminal decision | generic `LegacyChannelPersistenceEnvelope`; binding actor | approval v2 records and `teams-binding-snapshot-v2` | `ApplyLegacyPersistence`; recovery completion saves a v2 snapshot | invalid approval values fail recovery |
| `tpdc-v1`, `tpdi-v1`, `tpdr-v1` | destination capture, invalidation, delivery state | generic envelope; binding actor | destination/delivery v2 records and binding snapshot | `ApplyLegacyDestination` and `ApplyProactiveDeliveryRecorded`; recovery completion saves v2 | no destination values are invented; invalid or mismatched destinations fail recovery |
| `dads-v1` with Teams fields | activity fingerprints, approvals, destination, deliveries | generic envelope; binding actor | `teams-binding-snapshot-v2` | `ApplyLegacySnapshot`; recovery completion saves v2 | a payload without Teams fields remains the generic activity snapshot; invalid Teams fields fail recovery |
| `dtcam-v1`, `dtcais-v1` | channel activity map or index entries | generic envelope; channel conversation actor | channel map/snapshot v2 | `ApplyLegacyPersistence`; recovery completion saves v2 | invalid fingerprints or canonical channel sessions fail recovery |

## Migration and restart proof

The deterministic migration matrix is complete in
`TeamsPersonalRoutingTests`. It builds historical protobuf payloads with the
real legacy manifests, decodes them through `NetclawProtobufSerializer`, and
seeds the persistence test journal or snapshot store at explicit sequence
numbers. No fixture constructs a pre-converted v2 record.

- Journal-only legacy state recovers approval, destination, delivery, and
  routing state; the generated binding snapshot is `TeamsBindingSnapshot` v1;
  a restart suppresses the already routed activity.
- Legacy snapshot only, snapshot plus later legacy events, and legacy plus
  later v2 events all recover in order, compact to a Teams-owned v2 snapshot,
  and restart from that snapshot.
- Legacy channel index records compact to `TeamsChannelActivityIndexSnapshot`
  and survive restart.
- Forced migration snapshot failures leave binding migration health `Failed`.
  Repeated restarts retry from retained legacy journal records without state
  multiplication. A later successful save retains even a sequence-1 snapshot;
  compaction now deletes older snapshots only when one exists.
- Empty or insufficient legacy destination/snapshot payloads remain
  decode-only envelopes and cannot be emitted by the generic serializer. New
  Teams events and snapshots resolve only to serializer 151 v2 manifests.

## Current local validation

- Full Actors regression: 2,869 passed with no skips.
- Actors reminder, proactive, and serialization regression: 272 passed.
- Full Daemon regression: 1,142 passed with no skips.
- Teams-only Daemon regression: 172 passed.
- Focused scheduling/tool-policy regression: 48 passed with no skips.
- Focused Teams routing/current-session regression: 72 passed with no skips.
- All required Actors, Teams, Daemon, and Daemon.Tests builds passed with zero
  warnings and zero errors.
- `dotnet slopwatch analyze`: 0 issues.
- `git diff --check`, copyright-header verification, and strict OpenSpec
  validation passed.

## Live channel-root translation drift and correction

The initial dedicated live validation established personal destination capture,
one-time proactive delivery, and restart recovery. A bot-mentioned channel root
authenticated and reached the daemon but was rejected as
`unsupported_attachment_shape` before routing or reply generation.

The saved tenant fixtures covered channel roots and HTML rendering wrappers
independently, but not their combined current SDK representation. A new
sanitized fixture captures the structural distinction: a channel-root activity
can carry a scalar nonempty HTML rendering wrapper with a standard UTF-8
charset parameter. It is transport rendering metadata, not a model-visible
attachment, because it has no name, content URL, or embedded reference; the
canonical activity text remains the only model-visible text.

The classifier now accepts only `text/html` with no parameters or with UTF-8
charset parameters, in addition to the pre-existing scalar/no-name/no-URL/
no-reference requirements. Unknown parameters, structured content, names,
URLs, embedded references, file-download-info, Graph, SharePoint, OneDrive,
empty upload shells, and mixed unsupported attachments remain fail-closed.

The test project now links directly to the OpenSpec fixture directory and
requires an explicit matrix entry for every JSON fixture. Focused tenant-
evidence and Teams foundation coverage passed after the correction. The live
channel root now passes translation and reply delivery. The channel proactive
matrix and the attachment gate are complete.

## Live channel ACL identity reconciliation

Merged `dev` at `116d03d5` was exercised with a fresh owner-only state and a
bounded HMAC comparison keyed by a random mode-600 local key. The trace exposed
no identifiers. Tenant, sender, and channel matched exactly. Only team failed:
the protected runner had selected the authoritative directory object identity,
while the authenticated activity matched the same authoritative team's
distinct internal Teams identity. There was no whitespace, case, partial, or
other canonicalization equivalence.

This is configuration class A, not a translator or ACL-policy defect. The
protected runner now resolves exactly one approved team and channel through the
authenticated directory, converts the directory object to its internal Teams
identity, and fails closed on zero, multiple, missing, or unavailable results.
It does not learn or store an allow-list value from inbound traffic. Production
ACL code remains exact, tenant-bound, mention-gated, and default-deny.

A second fresh state proved all four configured/translated dimensions matched
exactly with one valid, nonduplicate candidate each. The ordered lifecycle was
`channel_root_received`, `channel_root_translated`, `channel_acl_allowed`,
`destination_captured`, `channel_root_routed`, two processing/final
`session_output_received` and `binding_output_correlated` pairs,
`outbound_request_created`, `sdk_send_started`, `sdk_send_completed`,
`provider_result_mapped`, `actor_result_received`, and terminal
`reply_terminal`. The provider category was `success`, and the exact requested
reply appeared in the same root.

The first same-root reminder request then failed with the closed category
`tool_not_allowed_for_audience_profile`; the bot explicitly reported that no
reminder was created. Server-side state showed zero pending reminders and no
reminder files. Per the terminal-gate rule, restart/no-resend, second-root, and
attachment cases did not run. Do not broaden audience or tool policy merely to
make the live test pass; use the established authorization process to approve
the intended channel profile before resuming.

The daemon and tunnel stopped cleanly. Temporary HMAC/lifecycle source was
removed, Slopwatch reported zero issues, and the repository returned clean.
The safe comparison trace and ephemeral key remain mode 600 outside Git for
audit. No production-code correction was made or committed.

## Channel reminder audience-policy reconciliation

The successful but denied channel root resolved to `TrustAudience.Public` in
`TeamsChannelAclPolicy.TryResolveAudience`, because its canonical
`team/channel` key and team key were both absent from the bound
`ChannelAudiences`. The
explicitly allowed sender still resolved to
`PrincipalClassification.TrustedInternal`; SDK provenance was verified, and
the shared-channel trust boundary remained `TrustBoundary.Public`.

The first environment-variable attempt and the subsequent owner-only JSON
dictionary attempt both failed identically in a fresh live state. Canonical
Teams identities contain `:`, which Microsoft configuration providers reserve
as a hierarchy delimiter in both environment variables and JSON keys. The
documented dictionary form therefore could not bind the live canonical key.
This is class B, Teams audience misclassification caused by a production
configuration-contract defect. It is not an ACL, trust, tool-policy, or
reminder-delivery defect.

The extend-only correction adds `Teams.ChannelAudienceOverrides`, an array of
structured `TeamId`, optional `ChannelId`, and `Audience` values. Identifiers
are configuration values rather than keys, so delimiters remain opaque. Exact
team/channel entries precede team-wide entries; duplicate matching structured
entries fail closed as `invalid_channel_audience`. The legacy
`ChannelAudiences` dictionary and the unmapped Public fallback remain intact.
Red tests prove delimiter-safe JSON binding and Team policy resolution before
the correction; focused policy tests also preserve legacy precedence and
duplicate rejection.

`ToolAudienceProfileDefaults.CreatePublic` excludes every scheduling tool.
`ToolAccessPolicy.IsToolExposed` therefore omits `set_reminder`, and
`ToolAccessPolicy.AuthorizeInvocation` independently returns
`tool_not_allowed_for_audience_profile` at the profile check before
`SetReminderTool.ExecuteAsync`. The Team and Personal defaults include
`set_reminder`, with no reminder-specific approval override; normal approval
policy still applies if an operator configures one.

The owner-only live runner derives the already approved team and channel from
an authenticated directory lookup and atomically writes one exact structured
Team override to the isolated mode-600 configuration. The identifiers do not
appear in Git or in the script text. Zero, multiple, missing, unavailable, or
duplicate results still fail closed. Public Teams channels remain unable to
schedule reminders, and ACL, mention, tenant, root, destination, attachment,
and tool-profile checks are unchanged. The final live matrix is CI-gated until
the configuration correction merges into `dev`.

The cross-channel contract is now explicit:

| Scope | Resolved audience | Reminder creation | Approval | Target path |
| --- | --- | --- | --- | --- |
| Teams allowed personal chat | Personal | allowed | profile policy; default auto | `current_session` |
| Teams allowed but unmapped channel root | Public | denied before invocation | none | none |
| Teams exact approved mapped channel root | Team | allowed | profile policy; default auto | `current_session` to captured root |
| Slack/Discord/Mattermost approved thread | Team when operator-vetted, unless explicitly overridden | allowed | profile policy; default auto | `current_session` |
| Any Public channel/thread | Public | denied before invocation | none | none |

PR 8 requires durable proactive delivery for an already authorized generic
reminder; it does not grant reminder creation independently of the source
audience. `current_session` is the supported production creation and target
mode for the now-Team Teams root. It retains the canonical session and captured
destination generation; it does not accept a different root or fall back to a
personal or top-level destination.

## Proof Pass 4 full-regression and architecture-audit evidence

The branch tip was checked against current `proxicon/dev`. The current dev tip
is an ancestor of this branch. The audit required no rebase.

Generic Actors retains only channel-neutral dispatch, reminder contracts, and a
decode-only legacy envelope. Its serializer cannot emit historical Teams
manifests. Teams owns all active persistence contracts, protobuf definitions,
serializer mappings, recovery conversion, destinations, delivery state, and
binding diagnostics.

The SDK package and SDK types occur only in the daemon Teams transport boundary
and its tests. The Teams project contains no scheduler or timer engine. It
receives generic reminder delivery messages through the registered gateway.
The full Actors regression includes other supported channel behavior.

The audit verified these durable rules:

- A reminder persists `Pending` before it persists `Sending`.
- A captured generation remains bound to its delivery key.
- A changed destination blocks an old-generation completion from delivery.
- A permanent result persists its terminal state and invalidation in one record.
- Recovery changes an uncertain in-flight send to `DeliveryUnknown`.
- Capacity rejects a new delivery key without deleting terminal evidence.
- Diagnostics derive from the binding owner and exclude destination values,
  request state, content, credentials, headers, tokens, and provider exceptions.

## Proof Pass 2 state-machine evidence

Proof Pass 2 establishes the remaining offline state-machine properties without
claiming live Teams validation. Generation one is the first accepted capture;
an identical capture is idempotent and a changed validated destination advances
the generation with checked overflow. Each reservation retains that generation.
Late output for an older generation is recorded only against its original key,
does not post to the refreshed destination, and cannot invalidate the newer
generation.

The `TeamsProactiveDeliveryRecorded` terminal event atomically contains both
`FailedPermanent` and matching-generation invalidation. Recovery before that
event leaves no partial permanent state; recovery after it preserves the
terminal record and removes only the matching destination. Immutable delivery
keys correlate concurrent outputs independently; late terminal completions are
ignored. The binding retains 1,024 delivery records, rejects a new key at
capacity, preserves `Sent` idempotency evidence, and rejects oversized or
future-generation snapshots. Sequence-64 snapshot compaction retains the
destination generation, terminal delivery idempotency record, and durable
duplicate state. An invalidated snapshot retains its last generation so the
next authenticated capture advances rather than reusing terminal history.

## Proof Pass 3 request-independence and diagnostics evidence

The production TestServer route exercises `UseTeams`, SDK request parsing,
translation, `TeamsIngressActorHost`, and durable binding capture. Its test
authorization seam changes only policy validation; it does not bypass the SDK
handler or actor route. After the response and request-scoped sentinel are
disposed, a generic reminder forwarded through the registered Teams gateway
delivers through the app-level reply-client seam using the persisted personal
or canonical channel-root destination. A cancelled HTTP request creates no
destination; later generic delivery fails closed without an outbound call.

`GetTeamsBindingProactiveDiagnostics` is binding-actor-owned and derives only
safe bounded state: feature/migration health, destination and invalidation
presence, pending and terminal retention, retryable/permanent/unknown state,
missing-target state, and capacity pressure. Its single-owner binding model has
no ambiguous current-target state. The DTO never contains destination values,
request values, content, credentials, tokens, headers, or provider exceptions.

## Known incomplete gates

- PR 8 has no incomplete live gate. Packaging and release work can proceed.

## Resume order

1. Start PR 9 packaging and operations work from current `dev`.
2. Do not repeat the completed PR 8 live matrix.
3. Keep the exact ACL, trust, audience, attachment, and destination policies.

## PR #16 policy correction and final live matrix (2026-08-10)

PR #16 merged as `aa8ff41b` after all required CI checks passed. The correction
retains exact tenant, team, channel, sender, mention, canonical-root, trust, and
destination checks. It adds an extend-only structured channel-audience
override so canonical Teams identities containing configuration delimiters are
stored as values rather than dictionary keys. Unmapped channels remain Public;
only an independently approved exact mapping resolves to Team.

A new owner-only isolated state copied only protected configuration, identity,
and keys. Personal text reply and a new channel-root reply passed. In the same
channel root, the normal generic `set_reminder` plus `current_session` path
created and delivered one reminder. A second reminder survived a clean daemon
restart, delivered once to that root, and did not resend after another restart.
A new root received its own text reply and reminder exactly once, while the
original root received no cross-root delivery.

The required final real-upload smoke failed the attachment security gate. A
new root with a qualified bot mention and a harmless small text upload produced
the normal processing indicator and the requested model reply. It did not show
a safe attachment rejection. This contradicts the documented requirement that
unsupported Graph-backed or unknown attachment representations stop before
actor, model, Graph, download, or staging work.

The daemon and development tunnel stopped immediately. No additional live
scenario ran, and the isolated owner-only state was preserved. The result does
not expose the actual SDK attachment representation and therefore does not
justify another speculative rendering-wrapper exception or correction. Obtain
one bounded, privacy-safe SDK-boundary structural capture in a separately
authorized diagnostic pass, add a sanitized red fixture, and restore the
fail-closed boundary through green CI before resuming packaging or release
progression.

## Final attachment gate result (2026-08-18)

PR #21 added the narrow attachment-boundary correction. PR #22 added the
configuration schema correction. Both changes merged into `dev`, and merged
CI passed. A fresh owner-only state then passed the protected doctor checks.
The focused Teams tests passed 87 tests. The focused schema and doctor tests
passed 43 tests.

The authorized smoke used one new channel root, one qualified bot mention,
and one harmless small text upload. The runtime received one Teams event. It
routed zero events and recorded one `attachment_shape_rejected` result. It
posted, failed, and rejected zero replies. Teams showed no `Processing` update
and no bot reply.

The isolated state contained no attachment staging directory, staged file, or
session log after the result. This proves that no actor or model session ran.
The pre-routing rejection also prevented Graph, download, and staging work.
The daemon and the existing development tunnel stopped immediately. No other
live scenario ran. PR 8 task 8.3 is complete, and the release gate is clear.

## Prohibited shortcuts

- Do not relocate serializers without legacy decoding.
- Do not write legacy manifests or add Teams persistence to generic Actors.
- Do not alter persisted test state to force recovery or add a test-only reminder
  endpoint.
- Do not perform live validation before the architecture gate passes.
