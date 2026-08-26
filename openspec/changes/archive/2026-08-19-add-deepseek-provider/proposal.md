## Why

Netclaw cannot configure DeepSeek's hosted API as a first-class provider with required API-key authentication. Generic configuration also lacks DeepSeek-specific reasoning and tool-loop behavior.

## What Changes

- Add `deepseek` as a first-class provider type.
- Require a DeepSeek API key and store it through the existing secrets path.
- Use DeepSeek's stable OpenAI-compatible chat and model endpoints.
- Map MEAI reasoning options to DeepSeek's wire fields.
- Preserve `reasoning_content` across tool-call turns.
- Add provider discovery, diagnostics, CLI, TUI, and operator guidance.
- Keep required tests independent of live DeepSeek credentials.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-model-providers`: Add the DeepSeek provider contract, authentication, discovery, reasoning, and tool-loop requirements.

## Impact

The change affects provider descriptors, provider plugins, the shared OpenAI-compatible transport, CLI and TUI provider catalogs, tests, and operator guidance.

The change adds no third-party SDK dependency. Session actors continue to use `Microsoft.Extensions.AI.IChatClient`.

The provider stores the API key only in the encrypted secrets path. Missing or invalid credentials fail visibly.

The MVP excludes OAuth, beta DeepSeek endpoints, automatic account creation, billing management, and mandatory live-provider tests.
