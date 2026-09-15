## Purpose

Provide a secure, self-hosted Microsoft Teams channel that routes permitted
personal and channel conversations into Netclaw without exposing transport
objects to the orchestration runtime.

## ADDED Requirements

### Requirement: Teams ingress is never registered without authentication

The daemon SHALL NOT register or expose the Teams activity endpoint unless
Teams is enabled and all authentication prerequisites required by the selected
credential mode are present. ClientSecret mode requires TenantId, ClientId,
and ClientSecret. A future managed or federated mode requires TenantId, ClientId,
and its own mode-specific prerequisites; it SHALL NOT be evaluated as though it
were ClientSecret mode.

#### Scenario: Teams integration is disabled

- **WHEN** `Teams.Enabled` is false
- **THEN** Teams SDK services are not activated for ingress
- **AND** `/api/messages` is not registered by the Teams integration

#### Scenario: Enabled Teams configuration is incomplete

- **WHEN** Teams is enabled but tenant ID, client ID, or the credential required
  by the selected authentication mode is absent
- **THEN** `AddTeams` and `UseTeams` are not invoked for Teams ingress
- **AND** the Teams activity endpoint is not registered
- **AND** the Teams integration reports a contained configuration failure
- **AND** no unauthenticated SDK handler accepts activity traffic

#### Scenario: Enabled Teams configuration is complete

- **WHEN** Teams is enabled and all authentication prerequisites for the
  selected credential mode are present
- **THEN** the Teams SDK is initialized
- **AND** exactly one authenticated Teams activity endpoint is registered

#### Scenario: Existing daemon route conflicts

- **WHEN** an existing daemon route conflicts with the Teams activity endpoint
- **THEN** startup or its focused test fails rather than creating an ambiguous mapping

### Requirement: Teams channel is opt-in, tenant-bound, and default deny

The Teams channel SHALL be disabled by default. When enabled, it SHALL accept
only SDK-authenticated activities from its configured tenant, process only
personal and channel scopes, apply the Teams ACL before model execution, and
expose contained health without leaking secrets. Unsupported or unauthorized
activity is rejected. With mention-only policy enabled, an unmentioned channel
message is ignored unless the same approved human previously established its
canonical root with a genuine bot mention.

#### Scenario: Activity from another tenant is denied

- **WHEN** an authenticated Teams activity has a tenant other than the configured tenant
- **THEN** it is rejected before session dispatch with a safe denial reason

#### Scenario: Unmentioned new or unknown channel message is ignored

- **WHEN** an otherwise allowed channel message omits the Netclaw mention while mention-only policy is enabled
- **AND** its canonical root is new or unknown
- **THEN** no session turn is created

#### Scenario: Unmentioned same-human reply continues an established channel root

- **WHEN** an approved human established a canonical channel root with a genuine bot mention
- **AND** the same human later replies in that root without a mention
- **THEN** the existing session receives the turn

#### Scenario: Allowed personal sender is dispatched

- **WHEN** an explicitly allowed user sends a personal Teams message while direct messages are enabled
- **THEN** the message is dispatched with explicit trust context

### Requirement: Teams conversations use canonical tenant-aware sessions

The Teams identifier codec SHALL be the sole builder/parser for the two-segment
session identity `teams~{base64url(tenantId)}~{scope}~{base64url(conversationId)}/{threadKey}`.
Accepted scopes are exactly `personal` and `channel`. Personal `threadKey` is
the literal `conversation`; channel `threadKey` is the canonical unpadded
base64url root activity ID. Raw tenant, conversation, and channel root activity
values SHALL be nonblank and limited before actor creation. There is no current
generic hard actor-name or persistence-key length limit in
`src/Netclaw.Channels/ChannelGatewayActor.cs`; any local Teams limit SHALL be
documented, tested, and reject rather than truncate or hash.

The codec SHALL reject malformed, ambiguous, padded, noncanonical,
slash-containing, or oversized values. Decode/re-encode SHALL return exactly
the canonical encoding. A missing channel root activity ID SHALL be rejected as
`missing_activity_id`; it SHALL NOT receive a synthetic fallback.

#### Scenario: Personal conversation continues after restart

- **WHEN** two valid messages arrive in the same personal Teams conversation across a daemon restart
- **THEN** both route to the same durable Netclaw session

#### Scenario: Channel replies share a root-thread session

- **WHEN** valid replies reference the same canonical Teams channel root activity
- **THEN** they route to one session
- **AND** another root activity in the same conversation routes to a different durable session

#### Scenario: Tenant isolation is preserved

- **WHEN** otherwise equal conversation identities are encoded for different tenants
- **THEN** they resolve to different session identities

### Requirement: Teams output is bounded and correlated

The Teams channel SHALL post replies to their originating conversation, preserve
ordering, avoid token-fragment spam, and keep each serialized output activity
within a validated Teams payload ceiling. Until tenant-backed evidence or an
authoritative SDK constant validates that ceiling, implementation SHALL not
claim a final limit; any conservative internal ceiling needs recorded evidence.
Chunking SHALL not lose or duplicate text, including Unicode/multibyte text and
card overhead. SDK types remain inside `Transport` and unsupported Markdown
degrades to safe text.

One processing message may be created and updated where supported. If final
update fails, the channel records the failure and posts exactly one correlated
final reply for that completion path; retry logic SHALL remain bounded.

#### Scenario: Oversized response is chunked

- **WHEN** a Netclaw response exceeds the validated Teams activity payload ceiling
- **THEN** it is emitted as ordered bounded messages without losing or duplicating text

#### Scenario: Processing update fails

- **WHEN** a final update to a processing message fails
- **THEN** the channel records the failure and posts one correlated final response

### Requirement: Teams attachments and text renderings are evidence-gated

The Teams channel SHALL accept plain activity text. It SHALL accept a formatted
text rendering only when the SDK attachment has a non-empty `text/html` scalar
(a CLR string or SDK JSON string element),
no name, no direct content URL or thumbnail URL, no Graph/provider embedded
reference, and no structured content. A non-Graph navigation reference inside
that otherwise bounded scalar rendering wrapper is formatting metadata, not an
attachment URL. The HTTP body limit bounds this rendering. The translator SHALL ignore wrapper
markup and use only canonical activity text for model input.

The tenant-backed upload fixture has an empty `text/html` shell. That shell
SHALL be rejected as `graph_backed_attachment_unsupported`. Graph, SharePoint,
OneDrive, and file-download-info references SHALL use the same result. All
other attachment shapes SHALL be rejected as `unsupported_attachment_shape`.
The channel SHALL not download, stage, or send file data to a model.

Safe channel telemetry SHALL record `plain_text_accepted`,
`teams_text_rendering_wrapper_ignored`, `attachment_graph_backed_rejected`,
`attachment_shape_rejected`, or `attachment_malformed_rejected` as applicable.
The telemetry SHALL not contain message text, markup, filenames, URLs, or IDs.

#### Scenario: Unsupported Graph-backed file is rejected

- **WHEN** an inbound Teams attachment is a SharePoint or OneDrive reference
- **THEN** it is rejected as `graph_backed_attachment_unsupported`
- **AND** no Graph request, staging operation, or model dispatch occurs

#### Scenario: Formatted pasted text uses a rendering wrapper

- **WHEN** a message has canonical activity text and an evidence-backed HTML rendering wrapper
- **THEN** the channel routes the canonical text once
- **AND** the wrapper markup does not enter model input or attachment handling

#### Scenario: An empty HTML upload shell is rejected

- **WHEN** a message has the empty HTML shell from the tenant upload fixture
- **THEN** the channel rejects it as `graph_backed_attachment_unsupported`
- **AND** no actor, model, Graph, download, or staging action occurs
