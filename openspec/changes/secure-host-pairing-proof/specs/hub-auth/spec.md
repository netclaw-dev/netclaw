This capability uses these [engineering glossary](../../../../../docs/spec/GLOSSARY.md) terms:

- [Authority](../../../../../docs/spec/GLOSSARY.md#authority)
- [Local-control proof](../../../../../docs/spec/GLOSSARY.md#local-control-proof)

## Hub Authority Boundary

| Input | Chat authority | Host pairing authority |
|---|---|---|
| Valid device token | Allowed | Denied |
| Valid bootstrap token | Allowed | Denied |
| Loopback source address | Exposure policy decides | Denied |
| Local-control proof | Not a hub credential | Not accepted by the hub |

## ADDED Requirements

### Requirement: The SignalR hub excludes host-only pairing authority

The SignalR hub SHALL support authenticated chat sessions.
The hub SHALL NOT expose pairing code generation or infer daemon-host authority from a connection address.

#### Scenario: Authenticated client uses chat functions

- **GIVEN** device `laptop` connects with a valid bearer token
- **WHEN** it creates or attaches to a chat session
- **THEN** the hub processes the chat request under the authenticated identity

#### Scenario: Client cannot invoke legacy code generation

- **GIVEN** device `laptop` connects with a valid bearer token
- **WHEN** it invokes `GeneratePairingCode`
- **THEN** the hub exposes no such method
- **AND** the daemon creates no pairing code
