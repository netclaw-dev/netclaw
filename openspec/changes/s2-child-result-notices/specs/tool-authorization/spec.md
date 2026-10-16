## ADDED Requirements

### Requirement: A child result notice is automated input by kind

The system SHALL classify a child result notice as automated input by the durable kind of the input. The principal of the notice is the principal of the child owner, which can be a person. The system SHALL NOT use that principal to decide whether the notice abandons an open approval. A notice SHALL wait while an approval is open, and a rebuilt waiting notice SHALL keep its kind.

#### Scenario: A notice does not abandon an open approval

- **GIVEN** Alice has an open approval and her child ends
- **WHEN** the notice is admitted
- **THEN** the approval stays open and the notice runs after the answer

#### Scenario: A rebuilt notice keeps its kind

- **GIVEN** a notice waits behind an open approval
- **WHEN** the session passivates and recovers
- **THEN** the notice still does not abandon the approval

#### Scenario: A person's message still abandons the approval

- **GIVEN** an open approval
- **WHEN** a person sends a new message
- **THEN** the approval is abandoned as before
