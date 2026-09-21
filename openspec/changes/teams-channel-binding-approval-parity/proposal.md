## Why

Teams now has the SDK, channel routing, and Adaptive Card foundations required
to participate in the same secure session lifecycle as the established channel
bindings. Its binding still duplicates lifecycle logic, omits the shared
prompt-injection gate, and treats a short-lived card token as a session denial.
This change closes those differences without granting Teams any policy authority
or broadening the default Team tool surface.

## What Changes

- Bring Teams binding behavior under the shared approval-response, pending
  lookup, output-delivery, and prompt-classification contracts where their
  transport-neutral interfaces fit.
- Preserve Teams-owned opaque card correlation, nonce hash, prompt locator,
  activity delivery, and replay state while making the session journal the
  authority for approval decisions.
- Reissue an expired Teams approval card while the generic pending approval is
  still live; an expired card cannot approve or deny the core request.
- Add Teams coverage to the cross-channel binding contract suite and focused
  card replay, expiry, classifier, and feedback-recovery tests.
- Document the bounded post-refactor test delta. No live tenant validation is
  performed by this change.

## Capabilities

### New Capabilities
- `teams-approval-transport`: Opaque Adaptive Card transport binding, replay
  protection, and card reissue behavior for a session-owned approval request.

### Modified Capabilities
- `channel-binding-parity`: Teams joins the common binding lifecycle contract
  for safe ingress, approvals, output delivery, and contract coverage.
- `netclaw-input-adapters`: Teams executable input uses the shared
  prompt-injection classifier and fails closed when it is unavailable.

## Impact

Affected implementation areas are `Netclaw.Channels.Teams`, minimal shared
channel components, Teams tests, and channel-contract documentation. The change
introduces no external service, Graph permission, tenant configuration, data
migration, live message send, or upstream push.

Security impact: Teams does not alter Netclaw core tool eligibility. Host shell
remains Personal-only. Card callback data stays opaque and is validated before
it reaches the session. The shared classifier blocks high-risk input and fails
closed when its detector fails.

Operational impact: normal Teams reply delivery reports failures to the session,
while typing remains best effort. Card reissue adds a bounded transport action
only for still-pending session approvals.
