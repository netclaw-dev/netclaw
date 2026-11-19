Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

## ADDED Requirements

### Requirement: Channel output belongs to the current initialized pipeline

The channel actor SHALL accept output only from its current initialized pipeline and current actor incarnation.
It SHALL reject obsolete output and termination before transport, journal, delivery-state, or actor-stop effects.
These identities SHALL remain actor-local and SHALL add no journal event.

#### Scenario: Current output reaches the transport

- **GIVEN** an initialized current pipeline and current actor incarnation
- **WHEN** that pipeline emits a file and a text reply
- **THEN** the channel actor delivers both through its provider transport

#### Scenario: Old output arrives during replacement drain

- **GIVEN** an old pipeline emits text during drain before a replacement starts
- **WHEN** the channel actor receives that output after replacement
- **THEN** it posts no old text and preserves the new turn's empty-output reply

#### Scenario: Replacement creation fails

- **GIVEN** replacement creation fails and no current pipeline is initialized
- **WHEN** old output or termination arrives with the retained numeric generation
- **THEN** the channel actor performs no output effect and creates no pipeline from that obsolete termination

#### Scenario: Actor restart repeats a numeric generation

- **GIVEN** a fresh actor incarnation initializes a pipeline with the same numeric generation as its predecessor
- **WHEN** output or termination from the previous incarnation arrives
- **THEN** the channel actor rejects it before any transport, journal, delivery-state, or stop effect

### Requirement: A successful replacement retires an obsolete retry

A new pipeline replacement SHALL retire the previous attempt's scheduled retry before it starts.
A failure of the new attempt SHALL still schedule its required retry.

#### Scenario: Another request succeeds before the old retry expires

- **GIVEN** a failed replacement schedules a retry and another replacement succeeds before that retry expires
- **WHEN** the earlier retry deadline passes
- **THEN** the obsolete retry does not replace or interrupt the healthy pipeline
