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
   src/Netclaw.Configuration/Providers/Descriptors/OpenAiDescriptor.cs
   ```

   The array should be ordered with the most capable/recommended models first.

4. **Build and verify**: `dotnet build` to confirm no compilation errors.

5. **Update the memorizer memory** (ID: `e3c8ac2b-0fa2-47d3-b9ff-50a78f2d2863`)
   if the limitation status has changed.

## Why This Exists

See the detailed memorizer memory for full context:
https://memory.testlab.petabridge.net/view/e3c8ac2b-0fa2-47d3-b9ff-50a78f2d2863

The Codex CLI public client ID (`app_EMoamEEZ73f0CkXaXp7hrann`) only grants
identity OAuth scopes. API scope names like `model.request` and `api.model.read`
are not valid OAuth scope values — the authorization endpoint rejects them.
All third-party tools (OpenCode, OpenClaw) use curated/hardcoded model lists
instead of live discovery for OAuth tokens.
