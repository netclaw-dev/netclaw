## Context

See `proposal.md` for motivation and the change specs for behavior. Netclaw
already has remote-chat descriptors, actor-owned session bindings, durable
session state, channel output renderers, reminder target resolvers, and shared
attachment staging. Discord's gateway hierarchy is reusable at the actor seam,
but Teams sends authenticated HTTPS activities and must not be represented as a
persistent socket transport.

## Goals / Non-Goals

**Goals:**
- Keep Microsoft SDK types at the HTTP transport boundary.
- Reuse Netclaw's session, trust-context, persistence, output, attachment, and
  reminder contracts.
- Make Teams configuration disabled-by-default and fail closed.
- Make activity deduplication, approval correlation, and destination data
  durable across actor passivation and process restart.

**Non-Goals:**
- Group or meeting chat, Graph-backed file retrieval, Graph destination
  discovery, user-delegated actions, SSO, tabs, message extensions, or
  multi-tenant SaaS distribution.
- Cloud registration, tenant approval, or Teams-app provisioning at daemon
  startup.

### Phase 1.1 runtime modernization record (2026-08-24)

Phase 1.1 replaces the original Teams SDK .NET 2.0.9 plugin host with the
stable 2.1.0 ASP.NET Core-native host. The 2.0 `App.Builder`, `AddTeams`,
`UseTeams`, and `AspNetCorePlugin` path is removed. `AddTeamsBotApplication`
and `UseTeamsBotApplication` register one native `/api/messages` endpoint;
the endpoint retains Netclaw authorization, body-size, and rate-limit
conventions.

SDK typed message, mutation, conversation-update, and Adaptive Card action
handlers are translated immediately to the existing Netclaw-owned contracts.
The translator remains the tenant, canonical identity, root, mention,
attachment, and bounded-destination security boundary. A small SDK middleware
preserves the authenticated request's `replyToId` across the 2.1 typed activity
projection, which otherwise omits that value. No SDK type crosses to actors,
sessions, persistence, approvals, or provider orchestration.

The native SDK resolves the current secret-backed `Teams` configuration through
its supported compatibility mapping to the `AzureAd` client-credential model.
This keeps the client ID, tenant ID, and `Teams.ClientSecret` ownership and
shape intact; it does not duplicate credentials, broaden tenancy, or enable an
unauthenticated development mode. Native SDK activity sources and meters, plus
ASP.NET Core request instrumentation, feed the existing Netclaw OTLP pipeline
without adding sensitive Netclaw attributes.

This is an offline implementation record, not live validation. It preserves
the existing public endpoint, Azure Bot registration, Teams package identity,
ACL policy, session semantics, approval authority, and persistence format. The
branch base is the rollback point; live cutover remains an operator-controlled
smoke gate.

## Decisions

### Use the Microsoft Teams SDK at the transport edge

Use the current C# Teams SDK package set and hosting route after a disposable
compatibility spike proves net10.0, Linux container, authenticated activity,
card action, update, and proactive-send behavior. The Teams SDK is selected
over Microsoft 365 Agents SDK because this change needs Teams-native mention and
channel-thread behavior. The spike is a hard implementation gate: no fallback
to Bot Framework v4, TeamsFx, or unauthenticated raw HTTP is allowed.

#### Phase 0 SDK compatibility record (2026-08-01)

The disposable net10.0 spike restored and published for `linux-x64` using
NuGet.org and `Microsoft.Teams.Plugins.AspNetCore` **2.0.9**. Its transitive
Teams packages are `Microsoft.Teams.Apps`, `Microsoft.Teams.Api`,
`Microsoft.Teams.Common`, `Microsoft.Teams.Cards`,
`Microsoft.Teams.Extensions.Hosting`, `Microsoft.Teams.Extensions.Configuration`,
and `Microsoft.Teams.Extensions.Logging`, all version 2.0.9. The hosting API
compiled as:

```csharp
using Microsoft.Teams.Plugins.AspNetCore.Extensions;

builder.AddTeams();
app.UseTeams();
```

The SDK exposes `IContext<IActivity>.Send`, `.Reply`, and `.Quote` for inbound
replies, while `ActivityClient.CreateAsync`, `.ReplyAsync`, `.UpdateAsync`, and
`.DeleteAsync` provide destination-addressed replies and updates. Adaptive Card
actions are represented by `AdaptiveCards.ActionActivity`; generic activity,
message update, message delete, and conversation update activity types are also
present. This confirms the transport API shape needed by the planned adapter.

The spike also established a non-negotiable guard: when no `Teams:ClientId` is
configured, SDK 2.0.9 logs that it will accept unauthenticated `/api/messages`
requests. Netclaw's enabled integration registration must therefore reject
missing tenant, client ID, or secret before `AddTeams`/`UseTeams` runs; an
unauthenticated SDK mode is prohibited.

The offline compatibility portion passed. Tenant-authenticated inbound activity,
actual channel root/reply identifiers, card round-trip, proactive delivery,
message update semantics, and attachment download authorization remain
tenant-backed smoke gates. They cannot be claimed from a local process without
an Entra app registration, Teams installation, and public HTTPS tunnel.

#### Phase 0.2 tenant transport evidence record (2026-08-02)

The opt-in tenant spike used a locally configured test application and public
HTTPS endpoint. Repository fixtures under
`src/Netclaw.Daemon.Tests/Fixtures/Teams/TenantEvidence/` are sanitized,
synthetic structural records of the observed boundary. They contain no real
tenant, application, user, team, channel, conversation, activity, endpoint,
filename, payload, token, credential, header, cookie, signature, or
authenticated URL from the test environment.

The following observations are proven by the sanitized capture and are locked
down by offline fixture tests:

- Authenticated personal messages matched the configured tenant and reached the
  PR 3 durable binding path. A repeated accepted personal activity did not add a
  second durable binding event.
- A channel root and each reply in its thread supplied the same canonical root
  identity as the `;messageid=` suffix of `conversation.id`; the suffix value
  matched the root activity ID. A second root supplied a different conversation
  and root identity. Ordinary channel messages had no `replyToId`, so PR 4 must
  fail closed when the suffix is absent or malformed rather than infer a root
  from `replyToId`.
- Bot mentions are structured `mention` entities. A qualifying entity's
  `mentioned.id` matched both `recipient.id` and `28:` plus the configured bot
  ID. PR 4 must select and remove only matching entity text spans, never replace
  a display name. Single and double-bot mention shapes were observed. The
  attempted bot-plus-user probe supplied a bot entity but the user token as
  literal text, so a structured user-mention entity remains unproven; literal
  non-bot text is preserved by the fixture parser.
- Channel `messageUpdate` and `messageDelete` retained the original activity ID
  and thread conversation identity. The delete carried no message text or
  mention entities. PR 4's activity-to-root index must resolve these operations
  from durable identity, not their content.
- Channel uploads exposed an empty `text/html` attachment shell without a name,
  safe direct URL, or usable file metadata at the SDK boundary. The attachment
  remains `graph_backed_attachment_unsupported`; no Graph fallback or permission
  is approved.
- Formatted text can expose a distinct non-empty `text/html` rendering beside
  canonical activity text. The SDK materializes its scalar content as either a
  CLR string or JSON string element. The translator ignores that markup as
  metadata only. It requires no name, direct content URL, thumbnail URL,
  Graph/provider reference, or structured content.
  The canonical activity text is the only model-visible text.
- Teams SDK plain reply delivery worked in the originating thread. The earlier
  non-SDK connector/test-harness path did not prove production delivery.
- Teams SDK Adaptive Card delivery and authenticated `Action.Execute` invokes
  worked. Every observed action received one terminal SDK response in the same
  thread. A later diagnostic sequence displayed two cards and both actions
  completed; this is recorded as a diagnostic artifact, not a duplicate-delivery
  defect because causality was not established.
- A detached app-level channel-thread proactive reply delivered once after its
  triggering request completed. The required non-secret destination comprises
  tenant, channel-thread conversation, root activity ID, and SDK-managed
  application routing. This proves channel-thread proactive delivery only.
- A bot-authored SDK message was created and then updated in place using the
  destination conversation plus the created activity ID. The transport needs
  those identities and the authenticated SDK request context; it persists no
  access token.

#### PR 7 live attachment validation record (2026-08-04)

An isolated persistence root reused the existing app identity and authorized
user while an allowlist policy exposed no tools or MCP servers. Plain text and
a VS Code-formatted paste each produced one routed turn and one terminal reply.
Both exposed the bounded HTML rendering wrapper and recorded only the safe
`teams_text_rendering_wrapper_ignored` metric; canonical activity text remained
the only model input.

A real channel file upload was received and rejected as
`attachment_shape_rejected`. Its receipt did not increase routed-event or reply
counts, and it created no model turn, Graph request, download, staging action,
or file-content ingestion. The observed upload did not match the known empty
HTML upload shell, so the generic fail-closed result is the expected outcome.

The tenant spike did **not** prove personal reply delivery or personal proactive
delivery. PR 3 deliberately has no Teams outbound delivery, and no further
interactive probe is justified after the temporary diagnostics were removed.
These remain explicit Phase 0.2 gaps.

For future output work, Microsoft documents an approximate 100 KB bot message
limit, recommends keeping messages within 80 KB, and reports HTTP 413 with
`MessageSizeTooBig` for an oversized message. PR 5 must enforce a serialized
payload ceiling at or below 80 KB, including card payload overhead; this is
documentation-backed guidance, not a universal maximum independently discovered
by the tenant test. See [Format your bot messages](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/format-your-bot-messages).

Foundation work permitted from the completed offline evidence is limited to the
`Netclaw.Channels.Teams` project, central package pinning, solution/project
references, `ChannelType.Teams` and exhaustive switch updates, disabled
descriptor/diagnostics, Teams options/schema, identifier codec, Netclaw-owned
immutable contracts, strict endpoint-registration guard, and offline
translator fixtures/tests. The following remain tenant-backed gates: canonical
channel root/reply derivation, proactive destination/address construction,
Adaptive Card round-trip and terminal update behavior, message update semantics,
authenticated attachment download shapes, serialized payload ceiling, and final
manifest/runtime assumptions based on observed Teams behavior.

### Translate at ingress and route through a non-transport actor parent

The daemon's authenticated `/api/messages` handler invokes
`TeamsSdkActivityTranslator`, which emits immutable Netclaw-owned ingress
records. `TeamsIngressActor` is a process-local router with only a bounded
in-memory duplicate fast-path and canonical conversation-child lookup; it owns
no durable activity state or socket lifecycle. `TeamsConversationActor` is one
actor per canonical Teams conversation. It owns deterministic binding-child
resolution and the bounded, oldest-first persisted activity-ID-to-root/session
mapping. This ownership is required because an edit or delete arrives with an
activity ID and must locate a root/session before a binding actor can be
selected. `TeamsSessionBindingActor` is one actor per personal conversation or
channel root thread. It persists processed activity IDs before pipeline
dispatch, pending approval state/nonce/expiry, output/card locators, and
session-specific reminder idempotency state;
it alone drives `ISessionPipeline` and the Teams reply contracts.

Alternative: call the pipeline directly from the HTTP handler. Rejected because
it bypasses actor serialization, session ownership, passivation behavior, and
the existing channel contract.

#### PR 2 ingress correction record (2026-08-01)

The authenticated tenant presented by the SDK is an explicit Netclaw boundary:
it must be nonblank and ordinal-equal to configured `Teams:TenantId`; an
optional activity conversation tenant must equal that authenticated tenant.
Mismatches are rejected with safe reason codes before the ingress actor.

PR 2's duplicate cache records an activity only after the next actor boundary
reports acceptance. Failures, cancellations, and unavailable/deferred outcomes
remain retryable; the cache is process-local and bounded, not durable.
Until PR 3 supplies the conversation/binding owner, the deferred boundary
reports unavailable and the connector remains degraded/not-ready. It does not
claim successful routing or session dispatch.

`ReceivedAtUtc` is daemon receipt time from `TimeProvider`. A separate optional
SDK-free `PlatformTimestampUtc` preserves the platform event time without
letting it redefine local receipt ordering. The translator's public/untrusted
trust context is provisional transport authentication only; PR 3/4 must derive
a final ACL/audience context before any pipeline dispatch.

#### PR 3 personal-binding durability record (2026-08-01)

PR 3 enables only personal activities. `TeamsIngressActor` delegates to the
real conversation sink only after transport validation. The sink and
`TeamsConversationActor` reject every non-personal activity before actor
creation, and final personal ACL evaluation is default-deny: direct messages
must be enabled, the configured tenant must match ordinally, and the sender
must exactly match a non-empty `AllowedUserIds` entry. The binding derives the
pipeline `ChannelInput` as `Personal` / `Personal` /
`TrustedInternal` with verified Teams provenance; the provisional PR 2 public
transport classification never reaches the session.

`TeamsSessionBindingActor` is the sole durable processed-activity owner. Its
retention is 1,024 IDs per canonical personal session and evicts the oldest ID
first. It persists a reservation before queue admission. A live pipeline write
failure or caller cancellation persists a release, so that activity can be
retried. After a process crash that occurs after the reservation commits but
before the pipeline accepts the input, recovery suppresses the retry; this is a
documented at-most-once local-admission trade-off, not an exactly-once external
or model-execution claim. The persisted reservation and release records use
new Netclaw protobuf types and do not contain Microsoft SDK types. They store
only fixed activity fingerprints. Ordered snapshots compact the journal after
snapshot success and retain at most 1,024 fingerprints. Older binaries do not
recognize the new durable manifests. They must not run against PR 3 Teams
binding state. Disabling `Teams.Enabled` is safe operational rollback. It does
not establish binary rollback compatibility for stored PR 3 binding state.

PR 3 uses `TeamsSessionIdentifierCodec` as the sole personal session builder;
actor names URI-escape the resulting canonical ID. Bindings may passivate and
recover their durable state. The cached conversation parent remains live so its
name remains a reliable owner for binding-child recreation. Pipeline options
use `OutputFilter.None` and no default delivery target: no Teams reply,
renderer, destination, card, attachment, or proactive operation is introduced
in this PR.

#### PR 4 channel routing record (2026-08-02)

PR 4 consumes only the Phase 0.2 channel prerequisite subset. A channel root
is parsed once from the bounded, unique `;messageid=` suffix of
`conversation.id`; `replyToId` is never used as a fallback. The opaque suffix
is the sole root thread key. A channel conversation actor owns a 1,024-entry,
oldest-first persisted SHA-256 activity fingerprint index. The value is the
canonical encoded Teams session ID. That value is reversible base64url
routing identity, not a secret or displayable raw activity payload: it is the
minimum bounded identity needed to resolve a known edit or delete without
storing the activity ID. The actor does not log the value. This permits a known
edit or delete to resolve safely without content routing or an LLM turn.

Channel ACL is fail closed: a message requires configured tenant, nonempty
allowed team and channel lists, exact team/channel matches, and an optional
nonempty sender allow-list match. The RSC package permission
`ChannelMessage.Read.Group` can deliver unmentioned channel messages, but it
does not broaden model dispatch. With mention-only enabled, a new or unknown
root without a qualified bot mention is ignored before binding creation. The
only exception is an unmentioned reply whose canonical root index contains the
SHA-256 fingerprint of the same approved human who established that root with a
genuine mention. Structured mentions remove only the exact entity spans that
match recipient ID and `28:` plus configured bot ID; display text and literal
user-like text cannot grant or remove access. Audience overrides select
`team/channel`, then `team`, then Public only after explicit access succeeds.

The Teams package `AppId`, `bots[0].botId`, and
`webApplicationInfo.id` must all equal the active Entra application (client)
ID configured as the Teams `ClientId` and `BotId`. An Entra object ID or a
Teams `28:` identifier is not interchangeable. Updating the manifest version
is insufficient on its own: the exact target Team must upgrade or reinstall
the package and its owner must accept the RSC request before Teams delivers
unmentioned messages.
PR 4 leaves all output, cards, attachments, Graph history, and proactive
behavior deferred. Personal reply and personal proactive transport evidence
remain open Phase 0.2 gates for PR 5 and PR 8.

### Preserve the two-segment session grammar

Existing reminder routing parses session IDs as `{channelPart}/{threadPart}` in
`src/Netclaw.Channels/ChannelGatewayActor.cs`; that implementation has no
hard actor-name or persistence-key length constraint today.
Teams stores its tenant/scope/conversation tuple in a slash-free first segment:
`teams~base64url(tenant)~scope~base64url(conversation)`. The second segment is
`conversation` for personal sessions or the base64url root activity ID for
channel sessions. The Teams identifier codec is the sole builder/parser and
rejects blank, malformed, oversized, or ambiguous values.

PR 1 applies a 1 KiB UTF-8 limit to each opaque raw tenant, conversation, and
root activity component (and the corresponding 1,366-character unpadded
base64url limit before decode). `ChannelGatewayActor` has no existing hard
actor-name or persistence-key limit, so these are bounded-resource guards, not
an undocumented Teams platform limit; values reject rather than truncate or
hash.

Alternative: use `teams/{tenant}/{scope}/...`. Rejected because current generic
gateway and reminder code would parse only `teams` as the conversation key.

### Use one authoritative durable owner per datum

`TeamsIngressActor` has no authoritative persistence. `TeamsConversationActor`
durably owns only the bounded activity-to-root/session mapping used before a
binding can be chosen; unknown edits/deletes are acknowledged, logged safely,
and never create an LLM turn. `TeamsSessionBindingActor` owns processed
activity-ID deduplication, approval correlation, output/card locators, approved
destination, and delivery state. Retention is bounded and oldest-first at the
owning actor. Existing pending-approval persistence is extended with stable
protobuf field numbering and backward-readable behavior; removed field numbers
are never reused. Serialization compatibility tests must state whether older
binaries ignore, retain, or reject new fields before binary rollback is claimed.

### Enforce authorization before routing

The endpoint relies on Teams SDK authentication, then translation records the
validated tenant and activity metadata. Actor policy checks enabled state,
tenant, supported scope, team/channel, user, mention, payload, and audience in
that order. Personal direct messages require both explicit enablement and an
explicit user allow-list entry; no empty-list implicit allow is permitted.

### Keep outbound and proactive APIs transport-owned

`ITeamsReplyClient` and `ITeamsProactiveClient` expose Netclaw-owned message
and destination records. SDK clients implement them in `Transport/`. A single
processing message may be updated; final-update failure sends exactly one
correlated final reply. The output chunker measures serialized payload bytes,
not character count.

Proactive delivery only targets an address captured from a prior authenticated
allowed activity. It persists no access token and performs no Graph discovery.
The durable delivery states are `Pending`, `Sending`, `Sent`,
`FailedRetryable`, `FailedPermanent`, and `DeliveryUnknown`. The default
recovery policy for `DeliveryUnknown` is an explicit pre-implementation decision
gate: it must be retry-with-operator-visibility or operator-review-only, never
an implicit retry or false success claim.

#### PR 5 output delivery record (2026-08-02)

PR 5 adds non-streaming replies for accepted personal and channel sessions.
`TeamsSessionBindingActor` owns one `SessionPipelineHandle` and one output
subscriber. It disposes the input stream on stop. A new actor creates a new
subscriber after recovery. The output callback captures the actor reference at
initialization. It does not access an expired actor context.

The binding holds the current destination only in runtime memory. It does not
persist a service URL, an SDK object, a token, or a destination registry entry.
The destination has a tenant, conversation, scope, HTTPS service URL, and
scope-specific identity. Personal delivery requires a user ID. Channel delivery
requires a team ID, channel ID, and canonical root ID. Each identity has the
existing 1 KiB UTF-8 bound. The service URL has a 2 KiB bound and has no user
info, query, or fragment.

The daemon captures an SDK context only after accepted transport translation.
The context store is bounded to 1,024 current destinations. It checks activity,
conversation, and service URL equality before storage. The actor and all
Netclaw contracts remain SDK-free. The daemon edge owns SDK send and update
calls through a narrow operations seam.

`TeamsOutputRenderer` preserves text and Unicode. It normalizes line endings
only. Whitespace-only output has no delivery. The renderer measures the UTF-8
bytes of the Netclaw text activity envelope: `type`, `text`, `textFormat`, and
an optional channel root `replyToId`. This is an application-payload
approximation. It excludes the SDK request envelope, headers, and transport
framing. The 80 KiB ceiling is an application admission guard, not a claim
about final SDK serialization. Tenant validation remains the release gate. It
emits ordered chunks up to 16 messages. It keeps all text. It rejects an
oversized Markdown link if a safe split is not possible. The stable oversize
reason is `output_too_large`.

One processing message can be created. The first final chunk updates that
message. The binding retains its returned activity ID only when it meets the
same 1 KiB identifier bound. If the update fails, the binding makes one normal
final-reply attempt. It does not retry after any delivery failure. The reply
client reports
`Delivered`, `Updated`, `RejectedTooLarge`, `Unavailable`, `Cancelled`, or a
safe failure result. It does not expose SDK exception data. Delivery telemetry
records a success only after an SDK success result.

PR 5 does not add personal proactive delivery. PR 8 owns persisted destination
selection, proactive sends, and their durable delivery state.

#### PR 8 proactive reminder delivery record (2026-08-08)

`TeamsSessionBindingActor` is the sole authoritative owner of one current,
tenant-bound `TeamsOutboundDestination` for its canonical personal session or
channel-root session. It captures or refreshes that destination only after
the authenticated activity has passed tenant, scope, canonical-session/root,
and personal or channel ACL validation. Destination capture happens before the
durable admission reservation. Rejected, ignored, malformed, duplicate,
update, delete, attachment-rejected, and unsupported activities do not capture
or replace it.

The persisted destination contains only the bounded tenant, conversation,
scope, required HTTPS service URL, and scope-specific identity: personal user,
or channel team, channel, and root activity ID. It contains no SDK context,
access token, credential, message body, raw activity, or URL query, fragment,
or user-info. A recovered binding validates all fields and requires that the
destination reconstruct exactly the binding session. A channel destination
always replies to its captured root. It never falls back to a top-level post,
a different root, or a personal conversation.

The generic current-session reminder workflow routes a canonical Teams session
to the registered Teams gateway, then to its owning conversation and binding.
It therefore resolves one explicit known destination; a missing destination
returns a typed unavailable result and makes no SDK call. There is no Graph
lookup, global last-destination selection, or cross-tenant/user/channel
discovery. Arbitrary user or channel target strings are not accepted as Teams
proactive destinations.

Reminder creation remains governed by the generic source-audience tool profile;
proactive-destination support does not grant scheduling authority. An unmapped
Teams channel resolves to Public even after its tenant, team, channel, sender,
and mention ACL checks pass, so the default Public profile excludes
`set_reminder`. An operator may map an independently approved canonical team
and channel identity to Team through the structured
`Teams.ChannelAudienceOverrides` array; the Team profile includes scheduling
and may use the normal `current_session` path. Canonical Teams identities can
contain `:`, so they MUST be configuration values rather than dictionary keys;
Microsoft configuration providers reserve `:` as a hierarchy delimiter.
This mapping does not alter the shared-channel Public trust boundary, trusted
principal derivation, or any tenant, ACL, mention, root, or destination check.
The live runner derives one exact mapping from its authenticated approved team
and channel records, writes it atomically to isolated owner-only configuration,
and fails closed on missing, duplicate, or ambiguous results. It never learns
the mapping from inbound traffic and never persists an identity in Git. The
legacy `Teams.ChannelAudiences` dictionary remains supported for identities
that do not contain configuration delimiters.

#### Channel reminder policy live result and attachment gate (2026-08-10)

PR #16 merged the structured Team audience override after its full CI matrix
passed. A fresh owner-only run proved that an explicitly approved channel root
can use the normal generic `set_reminder` tool with `current_session`, deliver
proactively to the originating root, survive a daemon restart, suppress a
post-delivery resend, and keep a second reminder isolated to its distinct root.
The default unmapped audience remains Public, and no ACL, trust, mention,
destination, or generic tool policy was broadened.

The required final live attachment smoke did not satisfy the existing
fail-closed contract. A root containing a real user upload and a qualified bot
mention reached normal processing and produced a model reply instead of being
rejected before actor dispatch. The captured behavior does not reveal which
SDK attachment representation was supplied, so it is not evidence for
broadening another rendering-wrapper predicate. Live infrastructure stopped
immediately and no further scenario ran. Packaging and release progression
remain blocked until a bounded, privacy-safe SDK-boundary capture identifies
the representation and a CI-gated correction proves that it cannot reach the
actor, model, Graph, download, or staging paths.

For each stable generic reminder delivery key, the binding persists `Pending`
before it records `Sending` and submits the turn to the normal session pipeline.
It persists `Sent` only after the Teams SDK accepts a post. Retryable local or
transport failures record `FailedRetryable`; a missing or expired remote
destination records `FailedPermanent` and invalidates only that binding's
destination. A repeated `Sent` key reports the already confirmed delivery
without another post. A process recovery converts every recorded `Sending`
entry to a persisted `DeliveryUnknown`. The selected policy is
operator-review-only: a repeated unknown key is not automatically resent and
is never reported as delivered. This prevents a false exactly-once claim during
the external-send crash window.

The daemon transport uses the authenticated application client for output
delivery. It does not cache an inbound SDK context, an HTTP context, a request
service scope, a request cancellation token, or a request-bound client.
Proactive sends use the daemon SDK app and the validated persisted destination.
Update operations are not proactive sends. Safe telemetry records only
classification codes for destination capture, missing destination, delivery
attempt, success, retryable failure, and permanent invalidation. It never
contains destination values, reminder content, SDK exceptions, or credentials.

#### Channel-root HTML rendering metadata (2026-08-09)

One local, owner-only, single-use SDK-boundary capture recorded the second PR 8
channel-root rejection without retaining message text, identifiers, URLs, or
payload content. The attachment was one non-null `text/html` JSON-string
wrapper with nonempty bounded scalar length, no name, direct content URL,
thumbnail URL, card, file-download metadata, provider metadata, or unknown
properties. It carried a non-Graph embedded rendering reference. The activity
also had a second non-mention SDK entity and no channel-data object; neither is
an attachment-classifier input. The previous sanitized fixture instead had a
parameterized wrapper with no embedded rendering reference and one entity.

The exact rejecting predicate was therefore the generic embedded-reference
flag, not the media-type parameter, entity count, or channel data. A bounded
`text/html` scalar wrapper is now rendering metadata when it has no name,
direct content URL, thumbnail URL, structured content, or Graph/provider
reference. This does not accept attachments generally: empty HTML upload
shells, file-download-info, Graph/SharePoint/OneDrive references, direct URLs,
named or thumbnail-bearing attachments, binary/structured/unknown content, and
mixed unsupported entries still fail closed before routing. The wrapper is
ignored after translation and never becomes model-visible content; canonical
activity text remains the sole user text. The temporary capture code was
removed before commit and its owner-only local fingerprint remains outside Git.

The binding actor resolves a reminder destination from its own durable state.
The current session resolves its one valid destination. An explicit destination
key must equal the canonical binding session. A missing, stale, invalidated,
cross-session, cross-tenant, scope-mismatched, or ambiguous destination fails
closed. The actor does not select a first, latest, or global destination.

Each delivery record stores the destination generation that existed at
reservation time. A changed validated inbound destination increments that
generation. An unchanged destination does not increment it. The actor rejects
a late output whose generation differs from the current destination. It records
the outcome against the original delivery key. It does not post to the refreshed
destination.

The first valid capture is generation one. Overflow is fail-closed through a
checked increment. A `FailedPermanent` delivery event includes the destination
invalidation flag, so terminal classification and matching-generation
invalidation commit together. Recovery from that event therefore cannot expose
the invalidated destination between two durable writes. An event for an older
generation remains terminal for its own key but cannot invalidate a newer
capture.

Snapshots retain `LastDestinationGeneration` even when invalidation has removed
the destination record. A recovered binding advances from that retained value
on its next accepted capture, preventing an old terminal delivery generation
from being reused.

Reminder correlation is the immutable delivery key carried by session output;
there is no binding-wide active reminder key. Terminal or unknown keys ignore
late output. The binding retains at most 1,024 delivery records, including
terminal records required to suppress duplicate sends. It rejects a new key at
capacity rather than evicting that idempotency evidence. Snapshots retain the
destination generation and every retained delivery record; recovery rejects
oversized records and delivery generations newer than the captured destination.

Binding diagnostics are authoritative actor state. They expose only a health
state, a migration state, bounded counts, capacity state, and safe reason
codes. They do not expose Teams IDs, service URLs, contents, credentials,
headers, tokens, or provider exception text.

`GetTeamsBindingProactiveDiagnostics` queries the recovered binding actor
directly. Its reply derives destination presence, invalidation, pending,
terminal retention, retryable/permanent/unknown delivery state, missing-target
state, and capacity pressure from that actor's durable state. A binding has at
most one current destination, so its ambiguous-target count is structurally
zero. Invalid recovered durable state fails actor recovery closed rather than
returning unsafe state. The DTO contains only enums, bounded integers, booleans,
and approved reason codes.

The inbound SDK callback translates immediately to `TeamsInboundActivity` and
awaits `TeamsIngressActorHost.SubmitAsync`; neither the translated contract nor
the actor dependencies accept an HTTP context, request scope/provider, SDK
context, or request cancellation token beyond that ingress attempt. The
TestServer proof disposes the completed response, scoped sentinel, and inbound
cancellation source before a generic reminder reaches the registered Teams
gateway. The later send succeeds through a singleton app-level reply-client
seam using only the durable destination and immutable reminder correlation.
The same proof covers personal and canonical channel-root destinations.

The new protobuf fields and manifests are append-only. Older binaries do not
understand the PR 8 durable events and must not be rolled back against a journal
containing them. Operators may instead disable Teams while retaining that state.
Historical Teams manifests remain decode-only through the generic serializer.
On recovery, the owning Teams actor converts valid legacy payloads and writes a
Teams-owned v2 snapshot. A failed migration snapshot leaves the journal intact
for a later restart retry. Compaction never deletes the newly saved sequence-1
snapshot. Invalid or insufficient legacy destination state fails closed; it
does not synthesize a destination.
Offline tests cover destination recovery, missing-destination refusal,
confirmed-delivery restart idempotency, and the `DeliveryUnknown` recovery
policy. Personal and channel-thread tenant proactive sends remain live test
gates and are not yet claimed as complete.

### Keep credentials secret in every surface

`ClientSecret` may participate in effective runtime binding for ClientSecret
mode but must never appear in normal configuration serialization, generated
sample configuration, schema examples, doctor output, descriptors, diagnostics,
health detail, telemetry, structured logs, exception messages, test snapshots,
OpenSpec fixtures, or committed configuration. Tests must cover the actual
remote-chat configuration and diagnostic surfaces used by the daemon.

## Risks / Trade-offs

- [Teams C# API differs from current documentation] → Phase 0 compiles and
  exercises the generated template before repository dependencies are pinned.
- [Public endpoint increases attack surface] → register only when enabled, use
  SDK token validation, endpoint size/rate limits, and no request-body logging.
- [Teams reply/thread fields vary by activity type] → maintain sanitized fixture
  captures and reject missing canonical identifiers rather than synthesizing.
- [Restarted cards can outlive pending state] → nonce/expiry validation returns
  a deterministic terminal card result.
- [Teams file URLs can require Graph or channel credentials] → support only
  spike-validated download shapes; deny other forms without hidden fallback.
- [Reminder retries duplicate user-visible posts] → persist delivery state and
  represent interrupted requests as `DeliveryUnknown`, not exactly-once.

#### PR 6 interactive approval record (2026-08-03)

PR 6 added a replay-safe binary approval path. It stores a correlation value,
a nonce hash, requester authority, expiry, prompt locator, and decision. It
does not store a raw nonce, card JSON, tool arguments, service URL, token, or
SDK object. The actor uses generated 256-bit nonces and 192-bit correlations.
It compares nonce hashes with `CryptographicOperations.FixedTimeEquals`.

The PR 6 card has generic text and `approve` or `deny` action values. The actor
checks the session, ACL, sender, prompt ID, correlation, nonce, action, expiry,
and terminal state. It records the decision before it sends a response. A card
update has one update attempt and one reply fallback. Presentation failure does
not reopen a decision. The binding retains at most 128 approval states.

#### PR 10 approval-option parity record (2026-08-19)

`ToolAccessPolicy` is the approval authority. It computes the ordered options
for each call. Teams must not recreate this policy. The adapter renders only
the options in `ToolInteractionRequest.Options`, in that order. Each action
carries the supplied canonical option key, correlation, and nonce. The card
payload remains correlation data, not authority.

The stable keys are `approve_once`, `approve_session`, `approve_always`,
`approve_everywhere`, and `deny`. An eligible shell call can offer all five.
A shallow or session-scratch scope omits `approve_always`. A messy or dynamic
call, and a session-scratch retry, offers only `approve_once` and `deny`.
Non-shell tools omit `approve_always`. An MCP `approve_everywhere` option uses
the supplied label `Always allow this tool`. Its decision saves a canonical-tool
grant, not a directory grant.

PR 10 stores the ordered offered keys in the pending Teams state before card
delivery. The state also stores only bounded facts needed for a terminal label.
It must not store raw arguments, request text, card JSON, SDK objects, or a raw
nonce. The action validator checks that the canonical submitted key exists in
this stored set before its consume transition. It then forwards that exact key
in `ToolInteractionResponse`.

The session journal also stores option keys. The binding must not query it or
recompute policy during an action. Such a cross-actor lookup cannot protect the
binding consume transition. The Teams state is the local authority for the card
that it posted.

A pre-PR 10 pending entry has no offered-key set. A new binary must treat its
action as unavailable and must not forward a decision. This is fail closed.
The translator may admit its legacy `approve` or `deny` token only to return
that terminal result. It must not map a legacy token to a canonical key.
New protobuf fields use unused append-only numbers. Current code must read old
journals. An older binary cannot process PR 10 card values safely. Operators
must disable Teams before a binary rollback where PR 10 approval state exists.

The card must give an operator useful bounded context. It shows the tool, a
bounded request display, and the supplied fixed option labels. It shows bounded
candidate verbs or patterns, the effective scope summary, complex-command state,
and adopted-context presence when supplied. The renderer uses existing display
and truncation contracts. It must not persist raw display data only for recovery.

The actor still records the terminal decision before feedback. A crash can lose
the continuation. It cannot repeat a tool decision. A terminal card update still
has one update attempt and one reply fallback. Tenant proof must cover every
option shape and its terminal display.

## Migration Plan

1. Land the disabled project/config/schema and descriptor first; existing
   channel behavior and persisted session IDs remain unchanged.
2. Register the endpoint and runtime dependencies only for enabled Teams.
3. Release personal, channel, approval, attachments, and reminders in separate
   PRs behind the same `Teams.Enabled` gate.
4. Setting `Teams.Enabled=false` is the primary operational rollback and
   removes Teams ingress after process restart while retaining persisted Teams
   state for diagnosis. Binary rollback across persistence-schema changes is
   supported only where serialization compatibility tests prove the target older
   version tolerates the newer stored state.
5. Do not archive the OpenSpec change until offline tests, opt-in tenant smoke,
   runbook, and schema/doctor contracts pass.
