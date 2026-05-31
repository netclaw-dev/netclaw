---
description: Retrieve the latest OpenAI model IDs and update the curated model list in OpenAiDescriptor. Use when OpenAI releases new models or the hardcoded list needs refreshing.
---

# Update OpenAI Curated Model List

OpenAI OAuth tokens (via the Codex CLI client ID) cannot call `/v1/models` —
the endpoint returns HTTP 403 "Missing scopes: api.model.read". This is an
OpenAI-specific limitation. As a result, `OpenAiDescriptor` maintains a curated
model list for OAuth-authenticated users.

## Steps

1. **Fetch the current model catalog** from OpenAI's docs:
   - Primary: https://developers.openai.com/api/docs/models/all
   - Codex-specific: https://developers.openai.com/codex/models

   Use `WebFetch` to retrieve the page and extract model IDs.

2. **Filter for chat-relevant models** — exclude:
   - Image generation models (`gpt-image-*`, `dall-e-*`, `chatgpt-image-*`)
   - Video models (`sora-*`)
   - Audio/realtime models (`gpt-audio-*`, `gpt-realtime-*`, `tts-*`, `whisper-*`)
   - Embedding models (`text-embedding-*`)
   - Moderation models (`text-moderation-*`, `omni-moderation-*`)
   - Deep research models (`*-deep-research`)
   - Deprecated/legacy models (`gpt-3.5-*`, `gpt-4-turbo-preview`, `babbage-*`, `davinci-*`)

   Keep: frontier chat models (gpt-5.x, gpt-4.1), reasoning models (o3, o4-mini),
   coding models (gpt-5.x-codex), and their mini/nano variants.

3. **Update the `CuratedModels` array** in:
   ```
   src/Netclaw.Providers/OpenAi/OpenAiDescriptor.cs
   ```

   The array should be ordered with the most capable/recommended models first.

4. **Build and verify**: `dotnet build` to confirm no compilation errors.

## Why This Exists

OAuth tokens obtained via the Codex CLI client ID (`app_EMoamEEZ73f0CkXaXp7hrann`)
**cannot call `/v1/models`** — the endpoint returns HTTP 403 "Missing scopes:
api.model.read". This is NOT fixable by requesting additional OAuth scopes:

- `model.request` and `api.model.read` are **not valid OAuth scope names** — the
  authorization endpoint rejects them as "invalid scope"
- The only valid scopes are identity scopes: `openid profile email offline_access`
- The client ID grants API access (chat completions) implicitly, but model listing
  is blocked

All third-party tools (OpenCode, OpenClaw, Codex CLI) use curated/hardcoded model
lists instead of live discovery for OAuth tokens.

### Responses API requirement

Codex OAuth tokens MUST use the Responses API (`/v1/responses`), NOT Chat
Completions (`/v1/chat/completions`). Chat Completions is deprecated for Codex
and returns `insufficient_quota` even when the user has quota remaining.

| Endpoint | OAuth Token | API Key |
|----------|-------------|---------|
| `/v1/models` | **BLOCKED** (403) | Works |
| `/v1/chat/completions` | **BLOCKED** (429 insufficient_quota) | Works |
| `/v1/responses` | Works | Works |

### References

- OpenCode implementation: https://github.com/anomalyco/opencode/issues/3281
- OpenClaw scope fix: https://github.com/openclaw/openclaw/issues/24720
- Codex models docs: https://developers.openai.com/codex/models
