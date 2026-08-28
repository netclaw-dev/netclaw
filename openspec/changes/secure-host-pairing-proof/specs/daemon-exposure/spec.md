Cross-cutting terms use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Host pairing recovery preserves the exposure mode

The host pairing procedure SHALL work in every exposure mode without a configuration change.
Recovery guidance SHALL NOT instruct an operator to switch temporarily to `local` mode.

#### Scenario: Tunnel-mode host recovery succeeds

- **GIVEN** the daemon runs in a tunnel exposure mode
- **AND** the host has access to the daemon key ring
- **WHEN** the operator runs `netclaw daemon pair`
- **THEN** code generation succeeds without an exposure-mode change

#### Scenario: Exposure-mode switch is not a recovery path

- **GIVEN** the host lacks a valid device token
- **WHEN** the operator reads the pairing recovery procedure
- **THEN** the procedure directs the operator to the local-control command
- **AND** the procedure does not direct the operator to change the exposure mode

