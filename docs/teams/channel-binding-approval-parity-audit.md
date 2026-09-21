# Teams channel binding and approval parity audit

Audit date: 2026-08-25

This audit compares the Teams binding with the authoritative OpenSpec channel
binding, approval-gate, input-adapter, audience, ACL, and session specifications.
It also compares the implementation with the Slack, Discord, and Mattermost
bindings and their shared contract fixtures.

## Parity matrix

| Requirement | Teams status | Evidence and required action |
| --- | --- | --- |
| Trusted input, tenant and conversation identity, ACL before session ingress | Already shared/compliant | `TeamsSdkActivityTranslator`, `TeamsPersonalAclPolicy`, and `TeamsChannelAclPolicy` produce Netclaw-owned contracts and gate routing before the binding. Preserve this boundary. |
| Mention-gated Team thread routing and durable ingress deduplication | Behaviorally compliant but duplicated | `TeamsConversationActor` and `TeamsSessionBindingActor` own durable routing and activity reservations. Keep the Teams transport/index state; do not replace it with another channel's identifiers. |
| Prompt-injection classification for every executable inbound message | Non-compliant | Teams dispatches accepted inbound text without `PromptClassifier`. Add the shared classifier before pipeline enqueue. High-risk text and detector failures must not create a model turn. |
| Thread-gap hydration and adopted context | Not applicable due capability | Teams has no safe, ordered, bounded history fetcher and no configured Graph history permission. Do not add Graph calls or permissions. The actor therefore accepts no unverified historical gap. |
| Session output completion, cursor/reminder bookkeeping, prompt clearing, and delivery feedback | Behaviorally compliant but duplicated | Teams currently implements a distinct output loop and reminder state. Retain Teams proactive delivery transport state, but use the shared output lifecycle where its contracts fit and report ordinary turn delivery failures to the session. |
| Fail-loud outbound transport feedback | Non-compliant | Teams records output failures but does not send generic delivery-failure feedback for ordinary session replies. Route ordinary reply failures through the shared `SafeTransportCall` behavior; typing remains best effort. |
| Pending approval matching, requester verification, cold-spawn forwarding, and exact selected key | Non-compliant | Teams independently validates card state and forwards a result. Use `PendingApprovalLookup` and `ApprovalResponseFlow` for the shared requester/session response lifecycle while retaining correlation, nonce hash, and card locator as transport state. |
| Earliest pending text response and text approval fallback | Non-compliant | Teams has interactive cards only. Support shared text parsing/cold spawn behavior where the channel can receive text, without changing the session-owned policy/options. |
| Approval card rendering and opaque callback payloads | Behaviorally compliant but duplicated | Teams must retain its Adaptive Card renderer, opaque correlation/nonce validation, prompt locator, and native terminal-card response. It must not derive policy, reorder options, or store raw nonce/tool arguments. |
| Consume-once replay handling | Non-compliant | Teams persisted `TeamsApprovalConsumed` before the session acknowledgement, so a feedback failure could strand a core pending approval. Use bounded forwarding state and write terminal consumption only after the session response. |
| Card-token expiry | Non-compliant | The current 15-minute transport expiry persists `expired` and forwards core `Deny`. Replace this with deterministic card reissue while the session approval remains pending. An expired card cannot authorize; it also cannot manufacture a core decision. |
| Generic approval pause/recovery | Already shared/compliant | The session approval bridge and journal own pending calls and wait indefinitely. Teams must use that authority rather than ending a session wait on card expiry. |
| Core tool eligibility | Already shared/compliant | `ToolAccessPolicy` intentionally keeps host shell Personal-only. Teams must not add an exception; approval presentation begins only after the core pipeline finds a tool eligible. |
| Persistent grant versus policy storage | Already shared/compliant | `ToolApprovalConfig` remains policy in `netclaw.json`; `ToolApprovalStore` remains grants in `tool-approvals.json`. Teams must only pass the exact selected session option and never mutate either store. |
| Cross-channel binding contracts | Non-compliant | Teams has targeted daemon tests but no subclasses of the shared channel contract bases. Add Teams fixtures for the compatible contract surface and update the contract inventory. |

## Implementation plan

1. Extend the Teams dependency contract additively with the shared injection
   detector and apply `PromptClassifier` before Teams can enqueue a model turn.
2. Refactor the Teams approval path to use shared pending-request lookup and
   session-response flow while preserving durable Teams card correlation,
   nonce-hash, locator, delivery, and replay records.
3. Convert card expiry into a reissue flow. Persist the new card binding before
   accepting it, reject stale nonce actions, and forward only an explicit option
   accepted by the session.
4. Preserve failed callback forwarding as bounded transport state, return a
   retryable same-option presentation, and re-drive uncertain forwarding after
   restart without executing the selected option twice.
5. Add contract coverage and focused Teams replay/expiry/injection/feedback
   recovery tests, then
   update the OpenSpec deltas and minimal Teams configuration documentation.

No live tenant or Graph history test is part of this change. The post-refactor
retest delta is limited to unit and integration evidence for the changed
inbound-classification, approval-reissue, and approval-feedback-recovery paths.

## Result after implementation

The completed change makes executable Teams ingress use `PromptClassifier`,
routes ordinary text/error/turn lifecycle through `ChannelOutputEngine`, and
uses `SafeTransportCall` for normal reply delivery. Native typing and proactive
reminders remain Teams-specific delivery concerns; typing is deliberately best
effort.

Adaptive Card correlation, nonce validation, offered-key validation, and
durable consume/replay state remain Teams transport state. Once that boundary
accepts a card action, the shared `ApprovalResponseFlow` owns requester lookup
and the session acknowledgement. Teams records a selected option only as a
bounded forwarding state until that acknowledgement arrives; feedback failure
returns a retryable same-option card and restart re-drives the same option.
Card expiry persists a replacement nonce hash and reissues a card without
emitting a core approval decision.

Teams has no authenticated, ordered, bounded history source, so gap hydration
remains not applicable. `TeamsSessionBindingContractTests` now places the
applicable SDK-free binding lifecycle in the shared contract suite: safe,
blocked, and unavailable ingress; approval requester and automation outcomes;
recovery; pipeline reinitialization; and delivery-failure supervision. The
focused `TeamsPersonalRoutingTests` retain Adaptive Card correlation, nonce,
expiry/reissue, and replay coverage. There is no claim that opaque Adaptive
Card callbacks support the text-only cold-spawn path used by other transports.
