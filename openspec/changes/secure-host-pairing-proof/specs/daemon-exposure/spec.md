This capability uses these [engineering glossary](../../../../../docs/spec/GLOSSARY.md) terms:

- [Authority](../../../../../docs/spec/GLOSSARY.md#authority)
- [Local-control proof](../../../../../docs/spec/GLOSSARY.md#local-control-proof)
- [Pairing code](../../../../../docs/spec/GLOSSARY.md#pairing-code)
- [Device token](../../../../../docs/spec/GLOSSARY.md#device-token)

## Recovery Flow

```text
current exposure mode stays active
  -> host CLI uses the direct daemon endpoint
  -> local-control proof creates a pairing code
  -> remote CLI exchanges that code through the exposed endpoint
```

The diagram is schematic.
It omits tunnel health checks and the remote exchange rate limit.

## Pairing Topology by Exposure Mode

```text
Host code creation
  host CLI + key ring -> configured daemon endpoint -> pairing code

Remote code exchange
  remote CLI + pairing code -> advertised proxy or tunnel endpoint -> device token
```

The diagram is schematic.
It omits proof validation, token hashing, and transport encryption.

| Exposure mode | Host changes mode | Host needs a device token | Remote exchange route |
|---|---|---|---|
| `local` | No | No | Direct daemon endpoint |
| `reverse-proxy` | No | No | Advertised reverse-proxy endpoint |
| `tailscale-serve` | No | No | Advertised Tailscale endpoint |
| `tailscale-funnel` | No | No | Advertised Tailscale endpoint |
| `cloudflare-tunnel` | No | No | Advertised Cloudflare endpoint |

The configured exposure mode selects reachability and remote authentication.
It does not select host code-creation authority.
Only a valid local-control proof grants that authority.

## ADDED Requirements

### Requirement: Host pairing recovery preserves the exposure mode

The host pairing procedure SHALL work in every exposure mode without a configuration change.
Recovery guidance SHALL NOT instruct an operator to switch temporarily to `local` mode.

#### Scenario: Tunnel-mode host recovery succeeds

- **GIVEN** the daemon runs in `tailscale-funnel` mode
- **AND** the host has access to the daemon key ring
- **WHEN** the operator runs `netclaw daemon pair`
- **THEN** code generation succeeds without an exposure-mode change

#### Scenario: Reverse-proxy host recovery succeeds

- **GIVEN** the daemon runs in `reverse-proxy` mode
- **AND** the host has key-ring access but no device token
- **WHEN** the operator runs `netclaw daemon pair`
- **THEN** the host creates a pairing code through the configured daemon endpoint
- **AND** the exposure mode stays `reverse-proxy`

#### Scenario: Remote reverse-proxy caller cannot claim host authority

- **GIVEN** remote traffic reaches the daemon through a reverse proxy
- **AND** the request appears to come from loopback
- **AND** the caller has no local-control proof
- **WHEN** the caller requests a pairing code
- **THEN** the daemon rejects the request
- **AND** the current exposure mode remains available for normal remote pairing

#### Scenario: Exposure-mode switch is not a recovery path

- **GIVEN** the host lacks a valid device token
- **WHEN** the operator reads the pairing recovery procedure
- **THEN** the procedure directs the operator to the local-control command
- **AND** the procedure does not direct the operator to change the exposure mode

#### Scenario: Temporary local mode is an invalid workaround

- **GIVEN** the daemon runs in `cloudflare-tunnel` mode
- **AND** the host has no device token
- **WHEN** the operator follows the recovery procedure
- **THEN** the operator keeps `cloudflare-tunnel` active
- **AND** the operator uses `netclaw daemon pair` on the host
