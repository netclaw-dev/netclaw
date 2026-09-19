# Teams personal chat: historical tool names

## Observed failure

A Teams personal greeting produced HTTP 400 from the OpenAI Responses API.
The provider rejected `input[84].name` because it did not match `^[a-zA-Z0-9_-]+$`.
The Teams endpoint accepted the activity and returned HTTP 200 before the model request failed.

Read-only inspection found two historical calls to `helpdesk-dev/helpdesk_capabilities` in the personal session snapshot.
The generated MCP catalog contained `netratel-dev`, but no `helpdesk-dev` catalog.
The personal session log contained the same provider error from August 27, 2026.
These observations explain why a new greeting could fail while separate channel threads still worked.
The inspection did not capture the provider request body, so the snapshot indexes are not exact provider input indexes.

## Cause and correction

Persisted tool calls use canonical names such as `helpdesk-dev/helpdesk_capabilities`.
Provider requests use aliases such as `helpdesk-dev__helpdesk_capabilities`.
See [the engineering glossary](../spec/GLOSSARY.md) for shared terms.

`SessionMessageAssembler` passes `ToolRegistry.ToLlmFacingName` to `ChatMessageConverter` when it reconstructs model input.
Previously, that resolver returned an unknown name unchanged.
An unavailable MCP registration therefore allowed the canonical slash to reach the provider.
Any channel with such history could encounter the same failure.

The resolver now retains the registered alias when a registration exists.
Otherwise, it uses the existing `LlmFacingToolName.FromCanonical` conversion and validation.
Historical name conversion no longer depends on the current MCP connection or tool catalog.
Names that cannot produce a valid alias still fail validation.

```text
Recovered canonical history
  -> session message assembler
  -> registered alias, or validated canonical-to-alias conversion
  -> provider function call with its original call ID and arguments
```

This flow is schematic and describes only model-input conversion.
It does not register tools, expose new definitions, execute historical calls, or grant access.
Tool dispatch and authorization still use the current registry and existing policy checks.
The change preserves snapshots, journal records, actor identities, tool results, and attachment data.

## Regression coverage

- Convert historical calls when the registry is empty.
- Preserve the same alias after an MCP server removes its tool registrations.
- Keep missing tools absent from lookup and grant-filtered exposure after conversion.
- Retain registered aliases, first-party names, and valid aliases.
- Reject names that fail the existing alias validation.
- Recover a session snapshot, append a greeting, and assemble its model input for Personal, Team, and Public audiences.
- Preserve the original snapshot, canonical history, arguments, call IDs, and result associations.

The regression tests use synthetic history and no live provider calls.
The conversion occurs only in the outbound representation; no data migration is required.

## Operator retest

After CI, merge and deploy the corrected build through the normal deployment process.
Keep the existing personal conversation and its history.
Send the original greeting, then test a personal image and a text-plus-image message.
Confirm that the provider no longer rejects the historical function name.
Then test a channel-thread greeting and image to confirm continued operation.

The absent MCP tool remains unavailable until its normal connection and registration return.
This correction permits valid historical input; it does not restore that tool service.
