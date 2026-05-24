## ADDED Requirements

### Requirement: Approval-capable adapters preserve prompt reconciliation handles across recovery

Adapters that render interactive approval prompts SHALL durably record the
channel-specific prompt handle needed to reconcile that prompt later. The
durable record SHALL be keyed by session identity and approval `CallId` and
SHALL survive adapter passivation and coordinated daemon restart.

The durable record SHALL contain only transport metadata needed to locate and
update the prompt (for example Slack `messageTs`, Mattermost `postId`, or
Discord `messageId`). Approval validity, requester authorization, and prompt
expiry classification SHALL remain session-owned and SHALL NOT be reimplemented
by the adapter.

#### Scenario: Slack records prompt handle for later reconciliation

- **GIVEN** Slack posts a `ToolInteractionRequest` as an interactive approval prompt
- **WHEN** the post succeeds
- **THEN** the adapter SHALL durably record the prompt's `messageTs` keyed by session id and `CallId`
- **AND** a later cold-spawned binding can look up that handle without having observed the original request in memory

#### Scenario: Mattermost records prompt handle for later reconciliation

- **GIVEN** Mattermost posts a `ToolInteractionRequest` as an interactive approval prompt
- **WHEN** the post succeeds
- **THEN** the adapter SHALL durably record the prompt's `postId` keyed by session id and `CallId`

#### Scenario: Discord records prompt handle for later reconciliation

- **GIVEN** Discord posts a `ToolInteractionRequest` as an interactive approval prompt
- **WHEN** the post succeeds
- **THEN** the adapter SHALL durably record the prompt's `messageId` keyed by session id and `CallId`

### Requirement: Cold-spawned adapters reconcile prompts from session-owned outcomes

An approval-capable adapter SHALL use the durable prompt handle record to
reconcile the original prompt message when it is cold-spawned and later
receives either a user approval response or a session-emitted
prompt-reconciliation outcome, instead of logging a permanent "redraw
skipped" outcome.

If no durable prompt handle exists, the adapter SHALL still forward the user's
response to the session and SHALL emit explicit diagnostics that prompt
reconciliation could not be performed. It SHALL NOT silently claim success.

#### Scenario: Cold-spawned Slack binding resolves original prompt after approval

- **GIVEN** a Slack thread binding has no in-memory `_pendingApprovalRequests` entry for a call
- **AND** a durable prompt handle exists for that call
- **WHEN** the user clicks the original approval button after recovery
- **THEN** the binding SHALL forward the response to the session
- **AND** SHALL update the original Slack message into its resolved or expired state based on the session outcome

#### Scenario: Cold-spawned adapter fails loud when prompt handle is missing

- **GIVEN** a cold-spawned approval-capable adapter receives a response for a call with no in-memory pending entry
- **AND** no durable prompt handle exists for that call
- **WHEN** the response is forwarded to the session
- **THEN** the adapter SHALL still forward the response
- **AND** SHALL emit explicit diagnostics that prompt reconciliation could not be performed

#### Scenario: Coordinated restart preserves approval-prompt continuity

- **GIVEN** a daemon restart is triggered while an interactive approval prompt is outstanding
- **WHEN** the daemon restarts and the adapter later receives the user's click on the original prompt
- **THEN** the approval response SHALL reach the recovered session
- **AND** the original prompt SHALL be reconciled into a terminal state instead of remaining visually pending
