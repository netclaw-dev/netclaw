## Context

See `proposal.md` for the problem statement.
The Teams SDK 2.1 `MessageActivityInput` serializer omits a null text property.
The current card path sets the text property to an empty string.
It therefore emits `"text": ""` with the card attachment.

The same SDK serializer fails when a `Dictionary<string, object?>` is stored as attachment content.
The source-generated context has no metadata for that runtime dictionary type.

The Teams standard schema accepts the current case variants for container styles and text colors.
The Teams Fluent `Icon` examples use Pascal-case values such as `Accent`, `Large`, and `Regular`.
The current table property `firstRowAsHeader` matches the published schema.

## Goals / Non-Goals

**Goals:**

- Create an attachment-only native activity for approval cards.
- Validate the exact SDK activity serialization before a transport request.
- Keep ordinary text and typing activity construction unchanged.
- Return safe failure stages to the existing Teams actor boundary.
- Log only the stage and underlying exception type.

**Non-Goals:**

- No session, tool, actor, callback, persistence, policy, or other-channel change.
- No `Action.Submit` or text-approval fallback.
- No package, runtime, deployment, or tenant configuration change.

## Decisions

### Build one native activity at the transport edge

The transport will create a `MessageActivityInput` from each outbound message.
Text messages will set text.
Approval cards will convert the dictionary tree to `JsonElement` before attachment construction.
They will not set text.

The SDK source-generated serializer ignores the null text property.
This avoids the empty `text` field in a card-only message.
It also gives the SDK serializer its supported `JsonElement` content type.
A non-empty fallback is not required because the card has bounded screen-reader text.

### Verify the SDK activity before the SDK request

The activity builder will call the native `ToJson` serializer for each outbound approval card.
The code will discard the JSON after validation.
The request path will use the same activity object.

This check covers the source-generated serializer and attachment envelope.
It does not claim that a tenant has accepted the message.

### Retain supported card value mappings

The card builder already emits supported values for the reviewed elements.
Action styles use their documented lower-case values.
The published schema accepts the current table key and the reviewed case variants.

The change will not add converters without an observed contract defect.
Tests will pin the values against the published contract and Teams Icon examples.

### Preserve failure detail as safe stage data

The Teams transport will wrap payload, activity, serialization, create, reply, and update failures in an internal stage exception.
The reply client will map the exception to a safe reason code.
The transport log will contain only the stage and exception type.

The actor receives no exception details or remote response text.
Existing size, invalid-destination, cancellation, and availability result mapping remains intact.

## Risks / Trade-offs

- [The preflight serialization has a small card-only cost] → The card path already has a serialized-size check and has low volume.
- [An external tenant still rejects a valid SDK envelope] → The safe stage log separates create, reply, and update from local construction.
- [A host differs from the published schema] → The owner live smoke remains the release gate.

## Migration Plan

1. Deploy the focused Teams transport correction.
2. Run the owner card smoke in a Personal conversation.
3. Run the existing Posts and Threads checks after the Personal result.
4. Roll back the binary if the host rejects the corrected message.

The change adds no persistence data or configuration value.
