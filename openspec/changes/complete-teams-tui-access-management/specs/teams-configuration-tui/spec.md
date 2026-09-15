## ADDED Requirements

### Requirement: Teams configuration has separate add and manage flows
The Teams configuration UI SHALL provide separate add and manage actions for destinations and principals.
The management views SHALL render saved canonical records before directory access.
The UI SHALL retain unresolved records and allow their exact removal.
The UI SHALL preserve global and exact-channel principal scope during all navigation paths.

#### Scenario: A directory request fails while records exist
- **WHEN** the directory service rejects a label request
- **THEN** the UI shows each saved canonical record and its removal action

#### Scenario: An operator leaves channel principal edit
- **WHEN** the operator presses Esc from a channel principal editor
- **THEN** the next principal edit uses the global scope

### Requirement: Teams search accepts ordinary text and owns its result
The UI SHALL pass ordinary letters, spaces, apostrophes, Unicode, and pasted text to a focused query input.
The UI SHALL provide advanced canonical-ID entry as a focusable action.
The UI SHALL invalidate a result when its query, selected scope, participant, or screen changes.
The UI SHALL publish a directory result only on the current UI loop and request generation.

#### Scenario: A query contains M
- **WHEN** an operator types `M` in a focused Teams query input
- **THEN** the query contains `M` and the UI does not open advanced entry

#### Scenario: A stale request completes
- **WHEN** an old directory request completes after its query changes
- **THEN** the UI does not display or select the old result

### Requirement: New manual principal IDs are canonical
The UI SHALL validate every new manual Teams user or group ID before it changes the draft.
The UI SHALL reject a mixed valid and invalid manual batch without a partial save.
The UI SHALL keep invalid legacy records visible and removable.

#### Scenario: A manual value is a display name
- **WHEN** an operator enters a Teams display name as a principal ID
- **THEN** the UI rejects the value before configuration persistence
