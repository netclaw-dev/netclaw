## ADDED Requirements

### Requirement: Named model role management
Model CLI and TUI operations SHALL assign roles by changing references and SHALL edit model metadata only through the selected definition.

#### Scenario: Assign existing definition
- **WHEN** an operator assigns an existing named definition to Main
- **THEN** only the Main role reference SHALL change
- **AND** no definition metadata SHALL change

#### Scenario: Mutating legacy configuration
- **GIVEN** the CLI loads a valid legacy model configuration
- **WHEN** a model mutation is requested
- **THEN** the CLI SHALL migrate and validate the canonical shape before persistence
- **AND** failure SHALL leave the original file unchanged
