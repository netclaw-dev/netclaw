## Why

The memory formation regression investigation exposed an ambiguity in the
adopted-context contract: some paths were treating any non-empty adopted window
as if it implied third-party participation, while other paths were using the
same signal for truthful audit provenance. Netclaw needs those concerns split so
approval and security artifacts stay factually complete, while automatic memory
suppression keys only off adopted context that actually includes someone other
than the current author.

## Source PRDs

- `PRD-002`: Gateway security envelope and truthful audit/security provenance.
- `PRD-007`: Agent memory formation, durable memory authority, and local-memory
  safety rules.
- `PRD-009`: Input adapter contract and transport-agnostic session handoff.

## What Changes

- Clarify that `HasAdoptedContext` means exactly: the adopted window is
  non-empty.
- Clarify that adopted-speaker provenance means all stable sender ids present in
  the adopted window, including self-only adopted history.
- Add a separate policy concept for third-party adopted context:
  `HasThirdPartyAdoptedContext` is true only when any adopted sender id differs
  from the current authorized author of the executable message.
- Keep approval, audit, and security provenance inclusive of the full adopted
  window whenever it is non-empty, even when the window contains only prior
  messages from the current author.
- Clarify that automatic memory suppression and related memory-formation caution
  rules key off third-party adopted context rather than mere adopted context.
- Preserve the existing trust model: adopted context is quoted,
  non-executable context; only the current authorized message is executable.
- Make the contract explicit and consistent across `thread-history-backfill`,
  `netclaw-agent-memory`, `netclaw-input-adapters`, `netclaw-session`, and
  `tool-approval-gates`.
- Out of scope: runtime implementation, migration of persisted data, or any new
  trust relaxation for adopted content.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `thread-history-backfill`: Clarify adopted-window semantics so non-empty
  windows set `HasAdoptedContext`, all adopted sender ids are preserved as
  provenance, and third-party adopted context is derived separately from the
  current author comparison.
- `netclaw-agent-memory`: Clarify that automatic memory suppression keys off
  third-party adopted context, while full adopted-window provenance remains
  truthful and explicit-elevation behavior is unchanged.
- `netclaw-input-adapters`: Clarify that threaded adapters hand off full
  adopted-window provenance plus distinct `HasAdoptedContext` and
  `HasThirdPartyAdoptedContext` semantics.
- `netclaw-session`: Clarify that persisted adopted-context records and session
  turn metadata keep the full adopted window truthful, derive third-party policy
  state separately, and preserve quoted/non-executable semantics.
- `tool-approval-gates`: Clarify that approval prompts and stored approval
  context stay inclusive of any non-empty adopted window, while policy-facing
  third-party-adopted state is tracked separately from full provenance.

## Impact

- **Security impact**: keeps approval and audit artifacts truthful without
  widening execution authority; third-party-sensitive policy remains explicit
  rather than inferred from an overloaded flag.
- **Operational impact**: implementers will need one additional policy concept
  in session/approval/memory metadata, but no new channel or ACL model.
- **Data model impact**: future implementation will likely touch adopted-context
  record shape, approval context shape, and memory-formation policy inputs so
  `HasAdoptedContext` and `HasThirdPartyAdoptedContext` cannot drift.
- **Code impact**: expected future work spans threaded adapter handoff, session
  persistence/projection, approval context construction, and automatic memory
  formation guards.
