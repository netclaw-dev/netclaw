# SPEC-002: Session Lifecycle and Protocol

Source PRDs: `PRD-001`

## Purpose

Define session identity, message protocol, persistence events, and compaction
behavior for `LlmSessionActor`.

## Session Identity

- entity key: `{channelId}/{threadTs}`
- one persistent actor per Slack thread

## Protocol Categories

- `Commands`: inbound intent from adapters or operator tooling
- `Events`: persisted domain state transitions
- `Broadcasts`: outbound notifications for subscribers

## Turn Lifecycle

1. `SendUserMessage` command accepted after policy checks.
2. actor appends user message to working context.
3. actor invokes configured chat client.
4. actor persists `TurnRecorded` event.
5. actor emits `TurnBroadcast` for subscribers.

## Compaction Lifecycle

1. threshold reached based on configured policy.
2. actor runs summarization reducer.
3. actor persists `SessionCompacted` event.
4. actor snapshots compacted state.
5. actor emits compaction broadcast for observability.

## Persistence Rules

- protobuf-net serialization only for events and snapshots
- framework-owned message envelopes only
- no direct persistence of `Microsoft.Extensions.AI` model types
