## Why

A resumed session can contain image content that the active model cannot accept.
The actor now discovers this mismatch only after it calls a provider.

Source PRDs: `PRD-001`, `PRD-005`
GitHub issue: `#1727`

## What Changes

- Check all active session media before each model call.
- Include recovered history, the new user message, and tool-produced media in the check.
- Reject unsupported or unknown media before the routing client receives a request.
- Show the unsupported modalities and clear operator recovery steps.
- Classify the result as an input compatibility error, not a provider failure.
- Do not activate a fallback model or a provider alert for this local error.

### In Scope

- Image, audio, and video compatibility checks for existing media records.
- A fail-closed result for unknown persisted modality values.
- Tests for recovery, new input, tool-loop input, and zero provider calls.

### Out of Scope

- A media proxy.
- Session model pins.
- An automatic switch to a compatible model.
- Audio or video feature support.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-model-capabilities`: Require a complete session-input compatibility check before each model call.

## Impact

The change affects the session actor, media conversion, error output, and session tests.
It does not change provider APIs or persisted media records.

### Security Impact

The check fails closed for unknown media types.
It prevents incompatible content from crossing the provider boundary.

### Operational Impact

Operators receive a local compatibility error with model-selection guidance.
Provider health alerts and fallback logs remain reserved for provider failures.
