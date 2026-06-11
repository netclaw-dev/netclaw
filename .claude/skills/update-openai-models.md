---
description: Verify Netclaw's OpenAI Codex live model catalog query when OpenAI or Codex changes model discovery behavior.
---

# Verify OpenAI Codex Model Discovery

OpenAI OAuth tokens (via the Codex CLI client ID) cannot call the platform
`/v1/models` endpoint — it returns HTTP 403 "Missing scopes: api.model.read".
Netclaw therefore uses the ChatGPT Codex backend model catalog for
OAuth-authenticated providers:

```
https://chatgpt.com/backend-api/codex/models?client_version=<codex-version>
```

The query sends the OAuth bearer token and `ChatGPT-Account-Id`. Discovery is
fail-closed: if the live catalog cannot be queried or omits context-window
metadata, Netclaw reports the error instead of using a stale built-in OpenAI
model list.

## Steps

1. **Check the official Codex CLI version** from the `@openai/codex` package.
   Netclaw's `CodexModelCatalogClientVersion` must track that package version,
   not Netclaw's own version.

2. **Query the live Codex catalog** with a temporary `NETCLAW_HOME` containing an
   OpenAI OAuth provider. Do not print OAuth tokens or account IDs.

3. **Verify discovery output** includes the expected picker-visible models,
   context windows, and input modalities. Missing context-window or
   input-modality metadata is a bug because Netclaw will fail closed rather than
   guess.

4. **Build and verify** with focused OpenAI provider tests and the normal repo
   gates.

## Why This Exists

OAuth tokens obtained via the Codex CLI client ID (`app_EMoamEEZ73f0CkXaXp7hrann`)
**cannot call `/v1/models`** — the endpoint returns HTTP 403 "Missing scopes:
api.model.read". This is NOT fixable by requesting additional OAuth scopes:

- `model.request` and `api.model.read` are **not valid OAuth scope names** — the
  authorization endpoint rejects them as "invalid scope"
- The only valid scopes are identity scopes: `openid profile email offline_access`
- The client ID grants API access (chat completions) implicitly, but model listing
  is blocked

The Codex backend gates model catalog entries by `client_version`; using an old
official Codex version can hide newer models even when the token is valid.

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
