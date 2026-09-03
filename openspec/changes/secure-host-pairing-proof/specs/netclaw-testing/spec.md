This capability uses these [engineering glossary](../../../../../docs/spec/GLOSSARY.md) terms:

- [Local-control proof](../../../../../docs/spec/GLOSSARY.md#local-control-proof)
- [Pairing code](../../../../../docs/spec/GLOSSARY.md#pairing-code)
- [Device token](../../../../../docs/spec/GLOSSARY.md#device-token)
- [Authority](../../../../../docs/spec/GLOSSARY.md#authority)
- [Durable and ephemeral](../../../../../docs/spec/GLOSSARY.md#durable-and-ephemeral)

## Required Proof Layers

```text
proof codec tests
  -> validator tests with virtual time
  -> real HTTP endpoint matrix
  -> CLI-to-daemon integration
  -> process smoke with daemon restart
```

The endpoint matrix SHALL execute real endpoint requests.
Labels that do not configure the tested exposure mode or credential are not sufficient.

| Example case | Expected result |
|---|---|
| `tailscale-funnel` + valid proof + no bearer | Code created |
| `reverse-proxy` + no proof + valid device bearer | Unauthorized; no code |
| `local` + cross-home proof + bootstrap bearer | Unauthorized; no code |
| Any mode + repeated proof | Unauthorized; original code unchanged |
| `reverse-proxy` + forwarded loopback + device bearer + no proof | Unauthorized; no code |
| Any remote mode + valid host proof + no device bearer | Code created |

```text
One authority rule
  +-> configured daemon route + valid proof, any mode -> success
  +-> proxy route + local address, no proof ----------> denial
  +-> any route + device token only ------------------> denial
```

The diagram is schematic.
It omits the proof format and the pairing-code exchange.

## ADDED Requirements

### Requirement: Pairing security tests prove host success and remote denial

The required suite SHALL test each exposure mode against every supported credential and local-control proof class.
Each denied case SHALL prove that no pairing code was created.
The required suite SHALL not require a live tunnel provider.

#### Scenario: Every exposure mode permits a valid host proof

- **GIVEN** the test matrix contains every supported exposure mode
- **WHEN** a host caller submits a valid proof in each mode
- **THEN** each case creates one pairing code

#### Scenario: Route metadata does not change the authority decision

- **GIVEN** the suite sends requests through direct and proxy-shaped routes
- **WHEN** each request changes its source address, forwarded headers, and device bearer
- **THEN** only requests with a valid local-control proof create a code
- **AND** each denied request leaves the prior code state unchanged

#### Scenario: Remote credentials never replace a host proof

- **GIVEN** the caller has no valid host proof
- **WHEN** the caller uses no token, a device token, or a bootstrap token in each exposure mode
- **THEN** every case denies code generation
- **AND** every case proves that no code exists

#### Scenario: Proof and key failure matrix remains deterministic

- **GIVEN** stale, future, changed, repeated, cross-home, malformed, and unsupported proofs
- **WHEN** the required suite validates each case with virtual time
- **THEN** every result matches the reviewed security matrix
- **AND** no case needs network access or a live tunnel process

#### Scenario: Process test proves durable state across restart

- **GIVEN** the current daemon stores a paired device in an isolated Netclaw home
- **WHEN** the process smoke restarts the daemon with that home
- **THEN** the prior device token still authenticates
- **AND** the host can create another pairing code
