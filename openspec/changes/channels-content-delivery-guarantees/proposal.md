## Why

Recent Slack content-drop bugs have cost real debugging time:

- `fix(memory): harden MagicByteValidator type poisoning silently dropping Slack images` (de3ba12, 2026-03)
- `fix(tools): stop inlining PDFs as DataContent on vision-capable models` (1fa5d75, 2026-04-13)
- **This week**: session `D0AC6CKBK5K/1776260335.865619` — user uploaded an image to a Slack DM, `SlackRoutingPolicy` returned `Ignore`, the only log we got was opaque `slack_event_filtered reason=routing_policy_ignore`, and Gemma was never invoked. Root cause could not be identified from logs alone because the policy has **seven** distinct `Ignore` branches and the single log message didn't say which one fired.

This change closes the diagnostic gap at the one site where silent drops actively cost hours: the Slack routing policy. It does nothing more. Scope was deliberately reduced during design and again during implementation after repeated "reduce, reduce" guidance from the owner.

## What Changes

- **Structured ignore reasons on `SlackRoutingPolicy`**
  - Convert `SlackRoutingDecision` from enum to `readonly record struct` with a `Kind` and a nullable `IgnoreReason`. **BREAKING** to the internal `SlackRoutingPolicy.Evaluate` return type; the only callers (`SlackConversationActor` and `SlackRoutingPolicyTests`) are updated in the same change.
  - New enum `SlackRoutingIgnoreReason { NoContent, WrongKind, HiddenMessage, UnsupportedSubtype, DmNotAllowed, DmMentionRequired, ChannelMentionRequired }` — one value per distinct `Ignore`-returning branch in `Evaluate`.
  - `SlackConversationActor` logs the specific reason on every `slack_event_filtered reason=routing_policy_ignore` emission and passes it to the existing `ChannelTelemetry.RecordSlackEventFiltered` telemetry sink as a structured reason label. Dashboard/alerting aggregate across branches without losing branch-level visibility.

- **Regression test coverage for realistic file-upload shapes**
  - `SlackRoutingPolicyTests` is updated so every existing `Ignore` assertion carries the expected `IgnoreReason`, plus new test cases for branches the existing suite did not cover: `HiddenMessage`, `BlockActionKind` (catches `WrongKind`), and three realistic Slack DM file-upload shapes — DM + `file_share` subtype + image, DM + files with no subtype, DM + hidden `file_share`. These three new cases are the shape class the 13:38 incident lived in.

**Out of scope (explicitly reduced during implementation):**

- `ContentDeliveryInvariant` at the session-actor LLM-invoke boundary. Would have caught drops between turn-accept and LLM-dispatch, but that is not where the 13:38 bug lives (it drops upstream at the Slack gateway, before any session exists). ~130 LOC for regression protection duplicating existing `SlackFileFlowIntegrationTests` pipeline coverage — not worth it.
- `InboundEventCapture` opt-in raw-payload capture flag. Required operators to run Netclaw connected to a live Slack workspace to seed fixture JSON — not a reusable contributor workflow. ~150 LOC.
- New `CaptureRawEvents` config property and JSON schema entry.
- Fixture replay harness, helper class promotion, and JSON fixture corpus. The `SlackFileFlowIntegrationTests` and `SlackThreadBackfillIntegrationTests` suites already provide pipeline coverage downstream of the routing policy, and SlackNet's public type layout is the authoritative schema source for inbound shapes — no fixtures needed.
- New `Netclaw.Channels.TestFixtures` test project.
- `ChannelDropReason` shared cross-channel enum, property-based fuzzing, live Slack smoke tests.

## Capabilities

### New Capabilities

None. The change lands entirely inside an existing capability.

### Modified Capabilities

- `netclaw-slack-socket`: Adds a new requirement for structured `SlackRoutingIgnoreReason` on every routing-policy ignore path, surfaced through the existing `ChannelTelemetry.RecordSlackEventFiltered` sink. Gives operators the diagnostic data needed to identify *which* policy branch killed a given inbound event instead of seeing only the opaque `routing_policy_ignore` bucket, and mandates unit test coverage for every branch so new branches cannot silently ship without a named reason.

## Impact

**Affected production code:**

- `src/Netclaw.Channels.Slack/SlackRoutingPolicy.cs` — decision record struct + reason enum + kind enum, factory methods, per-branch reason at every `Ignore` return site
- `src/Netclaw.Channels.Slack/SlackConversationActor.cs` — read `decision.Kind`, log the structured reason, pass to telemetry

**Affected test code:**

- `src/Netclaw.Actors.Tests/Channels/SlackRoutingPolicyTests.cs` — 17 existing assertions updated to check `decision.Kind` + `decision.IgnoreReason`, 5 new tests added (`HiddenMessage_IsIgnored`, `BlockActionKind_IsIgnoredAsWrongKind`, `DirectMessage_WithFileShareSubtype_IsRouted`, `DirectMessage_WithFilesNoSubtype_IsRouted`, `DirectMessage_HiddenFileShare_IsIgnoredAsHidden`)

**Security and operational impact:**

- **Breaking change:** `SlackRoutingDecision` changes from enum to record struct. Internal to `Netclaw.Channels.Slack`; no public API impact.
- **Telemetry:** The existing `netclaw.slack.events.filtered` counter now carries richer `reason` labels (e.g. `routing_policy_ignore:UnsupportedSubtype` instead of `routing_policy_ignore`). Existing dashboards continue to work; new dashboards can drill into the branch.
- **Failure behavior:** Unchanged. The routing policy still decides to route or ignore based on the same rules; only the returned shape and the logged reason are different.
- **Rollback:** Revert of a single commit. No data migration, no persisted state changes.
- **Config migration:** None. No new config properties.
