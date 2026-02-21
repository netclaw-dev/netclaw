# netclaw-slack-socket Specification

## Purpose

Define Slack transport behavior for Netclaw MVP using Slack Socket Mode.

## Requirements

### Requirement: Socket Mode transport

Netclaw SHALL use Slack Socket Mode as the primary transport for inbound and
outbound message handling in MVP.

#### Scenario: Socket session established

- GIVEN valid Slack app and bot tokens are configured
- WHEN Netclaw starts
- THEN it opens a Socket Mode connection
- AND reports connection health in operator diagnostics

### Requirement: Thread-bound reply delivery

Netclaw SHALL post assistant responses into the same Slack thread that produced
the session command.

#### Scenario: In-thread conversation

- GIVEN an allowed sender posts in thread `T`
- WHEN the turn completes
- THEN Netclaw posts the reply in thread `T`

### Requirement: No required inbound public webhook

Netclaw SHALL not require a public inbound HTTP endpoint for base Slack
transport operation.

#### Scenario: Local-only runtime

- GIVEN Netclaw runs with loopback-only binding
- WHEN Slack Socket Mode is connected
- THEN Slack interaction still functions for inbound and outbound messaging
