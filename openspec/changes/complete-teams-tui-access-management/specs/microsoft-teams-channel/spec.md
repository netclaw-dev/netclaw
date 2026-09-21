## ADDED Requirements

### Requirement: Group Chat metadata is display-only authority
The system SHALL persist only the canonical Group Chat ID in the allowlist.
The system SHALL use metadata only for display and selection.
The system SHALL not infer chat installation, membership authority, or ingress permission from metadata.

#### Scenario: A Group Chat has no topic
- **WHEN** Group Chat metadata has no topic
- **THEN** the UI shows a bounded participant preview or canonical ID suffix

### Requirement: Group Chat discovery does not enable ingress
The system SHALL keep Group Chat ingress disabled until an operator explicitly enables it.
The system SHALL not enable ingress when an operator only saves a canonical chat ID.

#### Scenario: An operator adds a chat while ingress is disabled
- **WHEN** the operator saves a valid Group Chat ID and leaves ingress disabled
- **THEN** the runtime denies Group Chat messages after configuration activation
