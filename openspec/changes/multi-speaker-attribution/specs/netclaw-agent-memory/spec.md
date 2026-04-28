## ADDED Requirements

### Requirement: Adopted thread context is not direct durable-memory authority

Adopted thread context SHALL be treated as ephemeral quoted context for model
reasoning, not as ordinary authoritative turn history for direct durable memory
writes, unless the current authorized message explicitly asks Netclaw to store,
correct, or otherwise elevate that information.

#### Scenario: Unauthorized adopted fact does not directly write memory

- **GIVEN** adopted context contains an unauthorized speaker claiming a new host
  password
- **WHEN** the authorized user does not explicitly ask Netclaw to store it
- **THEN** the adopted claim does not directly produce a durable memory write

#### Scenario: Authorized message may explicitly elevate adopted fact

- **GIVEN** adopted context contains a prior thread fact
- **AND** the current authorized message says to save that fact to memory
- **WHEN** memory policy otherwise permits the write
- **THEN** the durable memory path may proceed under the current authorized
  message's authority rather than the adopted speaker's authority
