## MODIFIED Requirements

### Requirement: Resolved audience propagated before session start

Channel adapters that mint or re-enter sessions SHALL resolve the inbound
`TrustAudience` before the session's first prompt/context assembly path runs.
The resolved audience SHALL be propagated into the first `GetSystemPrompt()` /
`ContextAssemblyInput` construction so the initial AGENTS variant, tool index,
and context layers match the channel policy from turn one.

#### Scenario: Slack-origin session uses resolved audience on first turn

- **GIVEN** a new Slack-origin inbound message resolves to audience `Public`
- **WHEN** the adapter or gateway creates the session's first turn
- **THEN** the first prompt/context assembly path receives `TrustAudience.Public`
- **AND** the initial tool/context index omits hidden Public capabilities

#### Scenario: Discord-origin session uses resolved audience on first turn

- **GIVEN** a new Discord-origin inbound message resolves to audience `Public`
- **WHEN** the adapter or gateway creates the session's first turn
- **THEN** the first prompt/context assembly path receives `TrustAudience.Public`
- **AND** the initial tool/context index omits hidden Public capabilities
