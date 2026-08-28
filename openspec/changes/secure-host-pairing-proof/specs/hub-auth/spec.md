Cross-cutting terms use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: The SignalR hub excludes host-only pairing authority

The SignalR hub SHALL support authenticated chat sessions.
The hub SHALL NOT expose pairing code generation or infer daemon-host authority from a connection address.

#### Scenario: Authenticated client uses chat functions

- **GIVEN** an authenticated client connects to the SignalR hub
- **WHEN** it creates or attaches to a chat session
- **THEN** the hub processes the chat request under the authenticated identity

#### Scenario: Client cannot invoke legacy code generation

- **GIVEN** any client connects to the SignalR hub
- **WHEN** it invokes `GeneratePairingCode`
- **THEN** the hub exposes no such method
- **AND** the daemon creates no pairing code

