## MODIFIED Requirements

### Requirement: TA-1 Trust context is explicit and fails loud

Every session turn SHALL carry an explicit turn context with a parsed audience
(`Personal`, `Team`, or `Public`), a requester, and a principal. Authorization,
consent, and dispatch SHALL use this turn context. The session journal SHALL
persist the turn context with each consent request, and a recovered request
SHALL use the persisted context, not the current session state.

An ingress that receives an invalid audience value SHALL reject the input
loudly (for example an HTTP 400 result or a CLI exit code 1). Where a
component falls back because an audience is missing or cannot be parsed, it
SHALL fall back to the narrowest audience, `Public`. No fallback SHALL select
a broader audience than the source provides. An audience derived from a
deployment default and a source audience SHALL be the narrower of the two.

A tool execution context SHALL hold the audience as a parsed value. Tool
authorization SHALL read that value and SHALL NOT parse a wire string again.

Planned change (owner decision, September 29): a missing or unreadable
audience becomes an error in every component. A follow-up code PR implements
it. Until that PR merges, the `Public` fallback above is the current behavior.

A turn without a message source SHALL NOT synthesize a requester. A consent
request without a recorded requester SHALL fail closed without a prompt,
except for a verified-automation principal.

#### Scenario: Invalid audience is rejected at ingress

- **GIVEN** a reminder create request with audience `admin`
- **WHEN** the daemon endpoint validates the request
- **THEN** it returns HTTP 400
- **AND** it dispatches no command

#### Scenario: Missing source audience does not broaden

- **GIVEN** a deployment default of `Personal` and a source audience of `Team`
- **WHEN** Netclaw derives the effective audience
- **THEN** the effective audience is `Team`

#### Scenario: Turn without a source cannot ask for consent

- **GIVEN** a turn with no message source
- **WHEN** a tool call requires consent
- **THEN** the call fails closed without a prompt
- **AND** Netclaw does not create a requester

#### Scenario: Tool authorization reads the parsed audience

- **GIVEN** a tool execution context with the parsed audience `Team`
- **WHEN** authorization evaluates a tool call
- **THEN** it uses `Team` without a string parse
- **AND** it applies no parse-failure fallback

A fresh buffered input SHALL adopt its canonical admitted turn context only at durable task consumption.
The session SHALL persist that adoption before a new model request or tool dispatch.
Old in-flight calls and approval prompts SHALL retain their original immutable requester and authority.
Automatic continuations SHALL retain the original task context.
The model SHALL NOT supply an adopted authority record.

#### Scenario: Buffered input cannot change an in-flight approval requester

- **GIVEN** an old tool attempt has an approval prompt for requester `U1`
- **WHEN** the session admits fresh buffered input from requester `U2`
- **THEN** the old attempt still requires its recorded `U1` authority
- **AND** fresh input cannot answer or expand that old attempt

#### Scenario: New work uses durable admitted authority

- **GIVEN** the prior batch ends and the session consumes an admitted buffered request
- **WHEN** the session starts the next model request
- **THEN** durable adoption already names the canonical admitted requester and context
- **AND** subsequent tool calls use the existing context deriver for that context


A legacy approval redrive SHALL use its original canonical approval context.
Its metadata-only baseline SHALL compare the full authority record with the existing approval owner.
The existing adopted task context SHALL retain that same authority after the approval state expires.
The baseline SHALL NOT create a requester, grant, or input activation.

#### Scenario: Legacy redrive preserves the original authority

- **GIVEN** a pre-change parked call has a canonical pending approval context for requester `U1`
- **WHEN** the runtime commits the first redrive baseline
- **THEN** the full persisted authority equals that original approval context
- **AND** a different requester or source scope cannot replace it
- **AND** snapshot recovery retains the original requester without a new input activation
