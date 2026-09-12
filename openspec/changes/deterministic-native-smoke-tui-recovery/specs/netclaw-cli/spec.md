## ADDED Requirements

### Requirement: Chat TUI ends generation after a terminal error

The Chat TUI SHALL clear its generation state after it receives terminal error output.
The TUI SHALL remove pending tool interaction state and enable input when the daemon remains connected.
The TUI SHALL show an explicit retry-ready status and request a redraw.

#### Scenario: Provider error ends generation

- **GIVEN** the Chat TUI shows `Generating...`
- **WHEN** it receives terminal error output from the daemon
- **THEN** it does not show `Generating...`
- **AND** it shows `Last request failed. Ready to retry.`
- **AND** input is enabled
