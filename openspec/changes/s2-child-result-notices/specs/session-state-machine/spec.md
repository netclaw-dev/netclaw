## ADDED Requirements

### Requirement: Passivation waits for an ended run that has no notice

The idle passivation SHALL keep the session while a child run is live or has ended without a notice. The session SHALL admit the notice after the result write ends.

#### Scenario: The session stays for a held result write

- **GIVEN** an ended run whose result write is held
- **WHEN** the idle timeout arrives
- **THEN** the session stays and admits the notice after the write ends
