# hub-auth Specification

## Purpose

Define the authentication framework for the SignalR hub, including the loopback
scheme, claims-to-principal mapping, and connection identity propagation.

This capability uses these [engineering glossary](../../../docs/spec/GLOSSARY.md) terms:

- [Authority](../../../docs/spec/GLOSSARY.md#authority)
- [Local-control proof](../../../docs/spec/GLOSSARY.md#local-control-proof)
- [Pairing code](../../../docs/spec/GLOSSARY.md#pairing-code)
- [Device token](../../../docs/spec/GLOSSARY.md#device-token)

## Hub Authority Boundary

| Input | Chat authority | Host pairing authority |
|---|---|---|
| Valid device token | Allowed | Denied |
| Valid bootstrap token | Allowed | Denied |
| Loopback source address | Exposure policy decides | Denied |
| Local-control proof | Not a hub credential | Not accepted by the hub |
## Requirements
### Requirement: Hub requires authentication

The SignalR hub SHALL reject unauthenticated connections. All hub methods SHALL
require a valid `ClaimsPrincipal` established by at least one registered
authentication scheme.

#### Scenario: Unauthenticated remote connection rejected

- **GIVEN** a connection originates from a non-loopback address
- **AND** no bearer token or other credential is provided
- **WHEN** the client attempts to connect to `/hub/session`
- **THEN** the connection is rejected with HTTP 401

#### Scenario: Authenticated connection accepted

- **GIVEN** a connection provides valid credentials for any registered scheme
- **WHEN** the client connects to `/hub/session`
- **THEN** the connection is accepted
- **AND** the hub methods are accessible

### Requirement: Loopback authentication scheme

The daemon SHALL register a loopback authentication scheme that automatically authenticates connections from `127.0.0.1` and `::1` as `LocalProcess` / `Operator` without requiring credentials only when the selected exposure mode allows loopback trust.

When `Daemon.ExposureMode` is `reverse-proxy`, the loopback scheme SHALL return no result for loopback requests so that only explicit credentialed schemes can authorize the connection.

#### Scenario: Loopback connection auto-authenticated in local mode

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1` or `::1`
- **WHEN** the client connects to `/hub/session`
- **THEN** the connection is authenticated with principal classification `Operator` and transport authenticity `LocalProcess`

#### Scenario: Non-loopback connection not auto-authenticated

- **GIVEN** a connection originates from a non-loopback address
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the scheme returns no result (defers to other schemes)

#### Scenario: Reverse-proxy mode does not auto-authenticate loopback

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** a connection originates from `127.0.0.1`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the scheme returns no result
- **AND** the connection must authenticate through a credentialed scheme instead

### Requirement: Claims-to-principal mapping

The daemon SHALL map ASP.NET Core `ClaimsPrincipal` claims to Netclaw's
`PrincipalClassification` and `TransportAuthenticity` types. The mapping
SHALL be centralized in a single service. Authentication schemes SHALL
produce Netclaw-specific claims that the mapper reads.

#### Scenario: Loopback claims produce Operator principal

- **GIVEN** the loopback scheme authenticated a connection
- **WHEN** claims are mapped to Netclaw types
- **THEN** `PrincipalClassification` is `Operator`
- **AND** `TransportAuthenticity` is `LocalProcess`

#### Scenario: Bearer token claims produce identified principal

- **GIVEN** a future bearer token scheme authenticated a connection with a
  device identity claim
- **WHEN** claims are mapped to Netclaw types
- **THEN** `PrincipalClassification` is `Operator`
- **AND** `TransportAuthenticity` is `Verified`
- **AND** a device identifier is available

#### Scenario: Unknown claims produce strict defaults

- **GIVEN** an authenticated connection has no Netclaw-specific claims
- **WHEN** claims are mapped to Netclaw types
- **THEN** `PrincipalClassification` is `UntrustedExternal`
- **AND** `TransportAuthenticity` is `Unknown`

### Requirement: Connection identity propagation

Every `MessageSource` created for a SignalR session SHALL carry the
authenticated identity from the connection's `ClaimsPrincipal`. The identity
SHALL include `PrincipalClassification`, `TransportAuthenticity`, and an
optional device or principal identifier as `SenderId`.

#### Scenario: Local session carries operator identity

- **GIVEN** a loopback-authenticated connection creates a session
- **WHEN** the session's `MessageSource` is constructed
- **THEN** `Principal` is `Operator`
- **AND** `Provenance.TransportAuthenticity` is `LocalProcess`
- **AND** `SenderId` is `"local"`

#### Scenario: Remote session carries device identity

- **GIVEN** a bearer-token-authenticated connection creates a session with
  device ID `"aaron-laptop"`
- **WHEN** the session's `MessageSource` is constructed
- **THEN** `Principal` is `Operator`
- **AND** `Provenance.TransportAuthenticity` is `Verified`
- **AND** `SenderId` is `"aaron-laptop"`

### Requirement: Auth framework is scheme-agnostic

The hub authorization, claims mapping, and identity propagation SHALL NOT
reference any specific authentication scheme. Adding a new scheme (bearer
token, OIDC/JWT) SHALL require only registering the scheme in DI and
producing the expected Netclaw claims — no changes to the hub, session
registry, or downstream policy code.

#### Scenario: New auth scheme requires no hub changes

- **GIVEN** a new authentication scheme is registered that produces standard
  Netclaw claims
- **WHEN** a connection authenticates via the new scheme
- **THEN** the hub accepts the connection
- **AND** claims mapping and identity propagation work without modification

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
