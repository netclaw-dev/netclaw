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

## ADDED Requirements

### Requirement: Host pairing recovery preserves the exposure mode

The host pairing procedure SHALL work in every exposure mode without a configuration change.
Recovery guidance SHALL NOT instruct an operator to switch temporarily to `local` mode.

#### Scenario: Tunnel-mode host recovery succeeds

- **GIVEN** the daemon runs in `tailscale-funnel` mode
- **AND** the host has access to the daemon key ring
- **WHEN** the operator runs `netclaw daemon pair`
- **THEN** code generation succeeds without an exposure-mode change

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
