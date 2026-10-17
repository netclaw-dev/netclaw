## ADDED Requirements

### Requirement: A failed post reports the turn that it belongs to

A channel binding SHALL send `DeliveryFailed` with the number of the turn whose output failed to post. The session accepts feedback only for its eligible turn (see `netclaw-session`, "A failed post on a reminder turn is visible to the operator"). A wrong number makes the session drop the report as stale feedback, or blame the wrong turn.

A binding posts the output of a turn while the turn runs. It learns the turn number from `TurnCompleted`. Thus the binding SHALL hold the first failure of the turn (kind and message) and SHALL send the report when `TurnCompleted` arrives, with `TurnCompleted.TurnNumber`. The binding SHALL NOT send the report when the post fails. The binding SHALL NOT use the number of the last completed turn, because that number names the previous turn.

All three bindings SHALL follow these rules:

- A binding sends at most one `DeliveryFailed` for each turn.
- A binding sends no report for a turn that posted at least one output. Part of the reply reached the thread, and a retry would post that part again. Slack follows this rule today. Discord and Mattermost SHALL follow it.
- A failed post of the empty-turn fallback is a failed post of the turn that the fallback answers. The binding SHALL report it with that turn number.
- A pipeline reinitialize abandons the turn in flight, so the binding SHALL discard the held failure.

The shared output engine and the binding actor own this decision. The held failure is actor-local. It lives for one turn.

#### Scenario: A failed post in turn 3 reports turn 3

- **GIVEN** a binding completed turn 2
- **WHEN** the output of turn 3 fails to post
- **THEN** the binding sends one `DeliveryFailed` with `TurnNumber = 3` when `TurnCompleted` for turn 3 arrives
- **AND** this holds for Slack, Discord, and Mattermost

#### Scenario: Counterexample - the report does not carry the previous turn

- **GIVEN** a binding completed turn 2 and no turn before it
- **WHEN** the output of turn 3 fails to post
- **THEN** no `DeliveryFailed` carries `TurnNumber = 2` or `TurnNumber = 0`

#### Scenario: Two failed posts in one turn send one report

- **GIVEN** turn 3 holds two text outputs
- **WHEN** both posts fail
- **THEN** the binding sends one `DeliveryFailed` for turn 3, with the kind and message of the first failure

#### Scenario: Counterexample - a turn with a posted output sends no report

- **GIVEN** turn 3 holds two text outputs
- **WHEN** the first post fails and the second post succeeds
- **THEN** the binding sends no `DeliveryFailed` for turn 3

#### Scenario: A failed fallback post reports the turn it answers

- **GIVEN** turn 3 produced no output, so the binding posts the empty-turn fallback
- **WHEN** the fallback post fails
- **THEN** the binding sends one `DeliveryFailed` with `TurnNumber = 3`

#### Scenario: A Mattermost reminder reply with a bad token reports the recorded turn

- **GIVEN** a `current_session` reminder runs in a Mattermost thread, and the session completes turn 1
- **WHEN** the Mattermost API rejects the bot token on the reply
- **THEN** the session receives one `DeliveryFailed` with `TurnNumber = 1` and `FailureKind = TransportFailure`
