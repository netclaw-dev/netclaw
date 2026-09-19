## Context

The Teams binding already owns opaque correlation, nonce hash, prompt locator, replay, and presentation recovery. The shared approval-response flow and the session actor own approval authority. See `proposal.md` for motivation.

## Goals / Non-Goals

**Goals:**

- Keep the renderer SDK-free and retain SDK payload work at the daemon transport edge.
- Emit the supplied modern visual language with schema 1.5 compatible elements.
- Preserve existing `Action.Execute` data and validation contracts.
- Show terminal presentation facts from bounded Teams state and the authoritative result timestamp.

**Non-Goals:**

- No Teams shell policy, command parser, risk classifier, approval grant, or session authority change.
- No `Action.Submit` fallback, review callback, tenant configuration, app package, or delivery route change.
- No sender display-name persistence. The current authenticated channel contract has no safe display-name value, so cards show `Authorized operator`.

## Decisions

### Keep an SDK-free presentation model

`TeamsApprovalCardRenderer` returns a Teams-only display model. The binding actor passes no SDK object into actor state or persistence. `TeamsAdaptiveCardPayloadBuilder` maps that model to deterministic JSON at the SDK edge.

The repository uses Microsoft Teams SDK 2.1. The inspected `Microsoft.Teams.Cards` asset is a separate 2.0.9 package and does not occur in the SDK 2.1 dependency graph. Adding that dependency would widen the package surface. The existing narrow dictionary builder remains the safe boundary.

### Use deterministic modern components

The builder emits a header `ColumnSet`, a semantic `Container`, a two-column `Table`, optional warning detail, and a terminal footer. The builder keeps the root card at version 1.5. It emits a useful `speak` value but never places correlation, nonce, or option callback data in it.

### Keep option and risk decisions outside Teams

The renderer iterates exactly the supplied session options. It maps only presentation styles. It does not use a local allowed-option list.

The current `ToolInteractionRequest` boundary has no canonical risk or impact field. The elevated renderer remains a covered presentation variant. Normal requests do not gain a risk row or elevated treatment.

### Render terminal facts without presentation persistence

The actor uses `TeamsApprovalConsumed.ConsumedAtUnixMilliseconds` for accepted and denied card timestamps. Expired cards use the card expiry timestamp. Existing bounded tool and request text survives actor recovery. No new durable fields are required, so no protobuf contract changes occur.

## Risks / Trade-offs

- [A Teams host lacks a modern visual element] → The payload stays within the 1.5 target and uses the supplied host-supported components.
- [A terminal callback lacks a safe display name] → The card shows `Authorized operator` and never exposes an opaque sender ID.
- [A future risk contract appears] → The Teams renderer can receive that canonical display value without adding a Teams classifier.
- [A card exceeds the host size limit] → Both the renderer model and the final payload check the existing serialized-size ceiling.

## Migration Plan

1. Deploy the Teams-only presentation change with the current Teams persistence state.
2. Verify pending, granted, denied, and expired cards in Personal, Posts, and Threads.
3. Roll back the binary if a host presentation fault occurs. This change writes no new persistence manifest or field.
