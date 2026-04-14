## Why

GitHub issue #640 documented a production failure where the `set_reminder` tool
accepted an invalid Slack channel ID (`D0AC6CKBK5K`, a session-ID fragment
the LLM confused for a real channel). The reminder saved successfully and the
failure only surfaced much later when the reminder fired and the notification
silently failed — blocking a time-critical task. `SetReminderTool` validates
every parameter *except* `reportToChannel`, giving the LLM no actionable
feedback when it supplies a bad target.

The fix surfaces invalid targets at reminder creation time, lets the LLM use
human-readable handles (`#general`, `@aaronontheweb`) instead of guessing
internal Slack IDs, and establishes a transport-agnostic resolver abstraction
so future channel transports can plug in without reshaping the reminder data
model.

## What Changes

- **Eager validation** of `reportToChannel` in `SetReminderTool` via a new
  `IReminderTargetResolver` abstraction. Unresolvable targets return an
  immediate tool error containing the resolver's message so the LLM can retry.
- **Human-readable names accepted**: `#channel-name`, `@username`, or raw
  Slack IDs (`C...`, `G...`, `U...`) all resolve to canonical identifiers
  before persistence. The stored `ReminderDefinition.ReportToChannel` is
  always the resolved ID, never the raw LLM input.
- **No-transport configurations fail loudly** rather than silently deferring
  the error: when the DI container has no `IReminderTargetResolver` registered
  and the LLM supplies `reportToChannel`, the tool returns a "no notification
  channel transport is configured" error. Reminders without a `reportToChannel`
  continue to work in headless/CLI configurations.
- **Slack adapter** (`SlackReminderTargetResolver`) wraps the existing
  `ISlackTargetResolver` and is registered alongside other Slack services.
- Auto-extracted session channel paths remain unchanged: session IDs are
  stamped by the channel framework and trusted as well-formed, so they skip
  the resolver to avoid a pointless API call.
- **Out of scope**: multi-transport routing (choosing between Slack and
  Discord at fire time) is tracked in issue #644. This change keeps the data
  model single-transport.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: the "Result reporting" requirement and the
  `set_reminder` tool contract gain new validation scenarios covering target
  resolution, rejection of unresolvable inputs, and the no-transport error
  path. The stored channel field semantics tighten — persistence now receives
  only canonical IDs.

## Impact

- **Code**
  - `src/Netclaw.Actors/Reminders/IReminderTargetResolver.cs` (new)
  - `src/Netclaw.Actors/Reminders/SetReminderTool.cs` (resolver injection,
    validation block, parameter description)
  - `src/Netclaw.Actors/Tools/ToolRegistrationExtensions.cs` (thread resolver
    through `WithReminderTools`)
  - `src/Netclaw.Channels.Slack/SlackReminderTargetResolver.cs` (new adapter)
  - `src/Netclaw.Daemon/Configuration/SlackChannelRegistrationExtensions.cs`
    (DI registration)
  - `src/Netclaw.Daemon/Program.cs` (resolve and pass to `WithReminderTools`)
  - `src/Netclaw.Actors.Tests/Reminders/SetReminderToolTests.cs` (new cases)
  - `feeds/skills/.system/files/netclaw-operations/SKILL.md` (updated
    `set_reminder` section + version bump)
- **APIs**: `set_reminder` tool parameter description updated to advertise
  human-readable targets. No change to JSON schema shape (string remains
  optional). No breaking change to existing stored reminders — the persisted
  field type is unchanged.
- **Dependencies**: no new NuGet packages. Relies entirely on existing
  `SlackTargetResolver` infrastructure.
- **Security / operational**: fail-loud behavior aligns with the
  secure-by-default posture in CLAUDE.md. Removes a silent-degradation path.
  No new authorization surface. No migration required for existing reminder
  files — validation only affects writes going forward.
