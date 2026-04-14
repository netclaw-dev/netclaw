## Context

`SetReminderTool` lives in `Netclaw.Actors` and is transport-agnostic by design
— the actor layer owns reminder scheduling but must not take a direct
dependency on any specific channel transport. Today it persists
`ReportToChannel` as a bare `string?` on `ReminderDefinition` and never
validates it. The channel-specific validation logic — `SlackTargetResolver`
in `Netclaw.Channels.Slack` — handles `#channel`, `@user`, and raw `C/G/U`
IDs but is only invoked at outbound message time by `SendSlackMessageTool`.
By then the LLM has moved on and the reminder is already persisted, so any
failure surfaces as a silent background error in `ReminderExecutionActor`.

Petabridge has committed to supporting additional channel transports beyond
Slack (see issue #644). Any fix that couples `SetReminderTool` directly to
`ISlackTargetResolver` would have to be unwound the moment Discord lands, so
the abstraction boundary has to go in now even if there's only one
implementation today.

## Goals / Non-Goals

**Goals**

- Surface invalid reminder notification targets as LLM-visible errors at
  `set_reminder` invocation time, not at reminder execution time.
- Accept human-readable Slack handles so the LLM doesn't guess internal IDs.
- Keep `Netclaw.Actors` free of any reference to `Netclaw.Channels.Slack`.
- Preserve existing reminder files and the public `ReminderDefinition` schema
  (no migration).
- Fail loudly, not silently, when `reportToChannel` is supplied in a
  configuration with no channel transport registered.

**Non-Goals**

- Multi-transport routing, transport prefixes (`slack:`, `discord:`), and the
  `ReportToTransport` field on `ReminderDefinition` — all tracked in #644.
- Existence validation via `conversations.info`. The resolver only performs
  shape checks on raw IDs and name-lookup resolution for `#`/`@` prefixes.
  A well-formed but nonexistent ID (e.g., a DM channel the bot has lost access
  to) still reaches execution. This is a known limitation.
- Changes to `ReminderExecutionActor`, `SendSlackMessageTool`, or any
  read-path component. Fix is strictly at the write path.
- Adding `D`-prefix support to `SlackTargetResolver`. Parked — the #640
  failure mode may or may not relate to DM IDs, and a separate investigation
  is needed before expanding resolver shape rules.

## Decisions

### Transport-agnostic `IReminderTargetResolver` interface in `Netclaw.Actors`

**Decision**: Define `IReminderTargetResolver` in
`Netclaw.Actors/Reminders/IReminderTargetResolver.cs` with a
`ReminderTargetResolution` result record. Implementations live in
transport-specific assemblies (Slack today, others later) and are registered
in the DI container at host composition time.

**Alternatives considered**:

- *Inject `ISlackTargetResolver` directly*: simplest, but couples
  `Netclaw.Actors` to `Netclaw.Channels.Slack` and violates the
  transport-agnostic boundary rule in CLAUDE.md. Would have to be unwound
  for #644.
- *Shape-check only (no resolver)*: no external dependency, but can't accept
  `#channel` or `@user` names and doesn't catch ambiguous raw inputs. Fails
  the issue's primary requirement.

### Nullable constructor parameter instead of a null-object default

**Decision**: `SetReminderTool` takes `IReminderTargetResolver? targetResolver
= null`. When the parameter is `null` AND the LLM supplies `reportToChannel`,
the tool returns an error. When `null` AND `reportToChannel` is omitted, the
tool works as before (pure in-session reminder).

**Alternatives considered**:

- *Register a `NullReminderTargetResolver` fallback*: was in an earlier draft.
  Violates the "no silent fallbacks" rule from the project memory — silently
  passing the raw input through to persistence reproduces exactly the #640
  failure mode. Rejected.
- *Make the resolver required*: breaks headless/CLI configurations where
  reminders are scheduled without a channel transport attached. These
  configurations are explicitly valid.

### Skip validation on auto-extracted session channels

**Decision**: When `reportToChannel` is empty and
`SetReminderTool.ExecuteAsync` auto-extracts the channel from
`context.SessionId`, skip the resolver entirely and use the extracted ID
verbatim.

**Rationale**: Session IDs are stamped by the channel framework, not by the
LLM. They're guaranteed well-formed by construction. Running them through
the resolver would add an unnecessary API call and couple the session-channel
path to resolver availability.

### Store the resolved canonical ID, not the raw LLM input

**Decision**: When resolution succeeds, `ReminderDefinition.ReportToChannel`
receives `result.ResolvedChannelId ?? result.ResolvedUserId`, not the raw
LLM-supplied string. This ensures downstream code (`ReminderExecutionActor`,
any future inspection tool, any future migration) always sees canonical IDs.

**Trade-off**: If an LLM supplies `#general` and an admin later renames the
channel, the stored ID keeps routing correctly — Slack IDs are stable. This
is an improvement over storing `#general` literally.

### Leave `NotifyInstructions` prose untouched

**Decision**: The existing auto-generated `NotifyInstructions` string at
`SetReminderTool.cs:99` interpolates `{reportToChannel}` into a default
message. Once we replace `reportToChannel` with the resolved ID, the
interpolation picks it up naturally. We do not modify LLM-supplied
`NotifyInstructions` — those stay verbatim, even if they reference the
original raw input in prose. The execution-time routing decision uses
`ReportToChannel`, not the prose in `NotifyInstructions`, so this is safe.

## Risks / Trade-offs

- **[Risk] Slack API call on every `set_reminder` with a named target** →
  Mitigation: `SlackTargetResolver` paginates channel and user lists. This
  is acceptable at reminder creation frequency (interactive, low volume).
  The raw-ID path bypasses API calls entirely via the early-return in
  `SlackTargetResolver.ResolveAsync`.

- **[Risk] Well-formed but inaccessible IDs still silently route to
  `send_slack_message` and fail at execution** → Mitigation: documented as
  a known limitation; existence validation via `conversations.info` is
  deferred. The common #640 failure mode (LLM passing a session-ID fragment
  or unresolvable handle) is still caught.

- **[Risk] Adapter layer duplication if a second transport lands before
  #644** → Mitigation: acceptable. The adapter is ~20 lines. Any second
  transport would need its own adapter anyway; #644 will formalize
  transport-keyed registration.

- **[Trade-off] The resolver result type collapses channel and user into a
  single `ResolvedId` field.** The adapter (e.g. `SlackReminderTargetResolver`)
  coalesces `channelId ?? userId` once at the transport boundary. A future
  change that needs to distinguish channel vs. user targets at the reminder
  layer would have to reintroduce that distinction — today the downstream
  path only reads `ReportToChannel` as a single routing target, so a single
  opaque identifier is sufficient.

## Migration Plan

No data migration required. Existing `~/.netclaw/reminders/*.json` files
retain their current `ReportToChannel` values and continue to execute via
the unchanged `ReminderExecutionActor` path. New validation only affects
writes going forward — the change is forward-compatible.

Rollback: revert the PR. Existing reminders persist unchanged; new reminders
that relied on name resolution would break, but none exist yet.

## Open Questions

None. Decisions locked during plan review.
