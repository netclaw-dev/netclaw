## Why

The required native smoke suite depended on an external local-model runtime and a downloaded model.
Model download, model speed, and tool support make unrelated pull requests fail.

The chat TUI also retains `Generating...` after a terminal provider error.
That stale state makes the init wizard tape depend on a transient render.

## What Changes

- Add a loopback-only OpenAI-compatible smoke LLM server.
- Use that server for the broad native tape and CLI smoke suite.
- Remove external local-model setup from the broad PR smoke path.
- Clear chat generation state and show a retry-ready status after `ErrorOutput`.
- Preserve bounded, prompt-free smoke request records in failure artifacts.

## Capabilities

### New Capabilities

- `netclaw-native-smoke`: Deterministic native smoke infrastructure uses a loopback OpenAI-compatible process.

### Modified Capabilities

- `netclaw-model-providers`: Required native smoke uses the existing OpenAI-compatible provider without a live model dependency.
- `netclaw-cli`: The Chat TUI presents a retry-ready state after a terminal session error.

## Impact

- Affected code includes the Chat TUI, native smoke scripts, tapes, scenarios, and CI workflow.
- The change adds a test-only executable and no production network listener.
- The smoke server binds only to loopback and records no request body or authorization value.
- Source PRDs: `PRD-004`, `PRD-005`.
