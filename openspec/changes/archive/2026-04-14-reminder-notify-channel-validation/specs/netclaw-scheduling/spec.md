## ADDED Requirements

### Requirement: Reminder notification target validation

The `set_reminder` tool SHALL validate any LLM-supplied `reportToChannel`
value through a transport-agnostic `IReminderTargetResolver` abstraction
before persisting the reminder definition. Validation SHALL accept
human-readable Slack handles (`#channel-name`, `@username`) and raw Slack
identifiers, and SHALL persist the resolver's canonical identifier — never
the raw LLM input. Unresolvable targets SHALL cause the tool invocation to
fail immediately with an error message the LLM can act on. When no
notification channel transport is registered in the host and the LLM
supplies a `reportToChannel`, the tool SHALL fail loudly with a
"no notification channel transport configured" error rather than silently
deferring the failure to reminder execution time.

#### Scenario: Hash-prefixed channel name resolved to canonical ID

- **GIVEN** a host with a registered `IReminderTargetResolver` that maps
  `#general` to channel ID `C0123ABC`
- **WHEN** the LLM calls `set_reminder` with `reportToChannel: "#general"`
- **THEN** the persisted `ReminderDefinition.ReportToChannel` equals
  `C0123ABC`
- **AND** the tool response reports success with the resolved schedule

#### Scenario: User handle resolved to canonical user ID

- **GIVEN** a host with a registered resolver that maps `@aaronontheweb` to
  user ID `U0456XYZ`
- **WHEN** the LLM calls `set_reminder` with `reportToChannel: "@aaronontheweb"`
- **THEN** the persisted `ReminderDefinition.ReportToChannel` equals
  `U0456XYZ`

#### Scenario: Raw channel ID passes through without an API call

- **GIVEN** a host with a registered resolver
- **WHEN** the LLM calls `set_reminder` with `reportToChannel: "C0123ABC"`
- **THEN** the persisted `ReminderDefinition.ReportToChannel` equals
  `C0123ABC`
- **AND** no directory lookup against the channel transport is performed

#### Scenario: Unresolvable target returns actionable tool error

- **GIVEN** a host with a registered resolver that cannot resolve
  `#nonexistent-channel`
- **WHEN** the LLM calls `set_reminder` with
  `reportToChannel: "#nonexistent-channel"`
- **THEN** the tool returns an error string beginning with
  `Error: Could not resolve reportToChannel`
- **AND** no `ReminderDefinition` is persisted
- **AND** no `SaveReminderCommand` is sent to the reminder manager actor

#### Scenario: No channel transport configured rejects supplied target

- **GIVEN** a host with no `IReminderTargetResolver` registered in DI
- **WHEN** the LLM calls `set_reminder` with any non-empty `reportToChannel`
- **THEN** the tool returns an error string containing
  `No notification channel transport is configured`
- **AND** no `ReminderDefinition` is persisted

#### Scenario: Auto-extracted session channel bypasses the resolver

- **GIVEN** a host with a registered resolver
- **AND** a tool execution context with a Slack session ID
  `C0123ABC/1234567890.123456`
- **WHEN** the LLM calls `set_reminder` without supplying `reportToChannel`
- **THEN** the persisted `ReminderDefinition.ReportToChannel` equals
  `C0123ABC` (extracted verbatim from the session ID)
- **AND** the resolver is not invoked

#### Scenario: Headless configuration with no supplied target continues to work

- **GIVEN** a host with no `IReminderTargetResolver` registered
- **WHEN** the LLM calls `set_reminder` without supplying `reportToChannel`
  and without a session ID that would trigger auto-extraction
- **THEN** the reminder is persisted with `ReportToChannel = null`
- **AND** the tool returns success

## MODIFIED Requirements

### Requirement: Result reporting

Task execution results SHALL be posted to the notification target stored on
the reminder definition. Notification targets SHALL always be canonical
identifiers produced by `IReminderTargetResolver` (never raw
LLM-supplied strings) for reminders created after this change. The system
SHALL support a silent-unless-notable mode where routine results are
suppressed and only notable findings are posted.

#### Scenario: Results posted to configured channel

- **GIVEN** a scheduled task has `report_to.channel` configured with a
  canonical channel ID
- **WHEN** the task execution completes with results
- **THEN** the results are posted to the configured Slack channel

#### Scenario: Silent-unless-notable suppresses routine results

- **GIVEN** a scheduled task is configured with silent-unless-notable mode
- **WHEN** the task execution completes with no notable findings
- **THEN** no message is posted to Slack
- **AND** the execution is logged as completed with no notable output

#### Scenario: Notable results always posted

- **GIVEN** a scheduled task is configured with silent-unless-notable mode
- **WHEN** the task execution produces notable findings
- **THEN** the results are posted to the configured Slack channel
- **AND** the findings are clearly presented
