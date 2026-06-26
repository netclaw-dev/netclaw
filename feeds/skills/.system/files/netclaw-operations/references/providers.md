# LLM & Search Providers


## LLM Providers


Netclaw routes chat completions through configured **provider entries** in
`secrets.json` / `netclaw.json`. Each entry has a logical name (operator-chosen)
and a `type` (well-known identifier). Manage them with `netclaw provider`:

| Subcommand | Purpose |
|------------|---------|
| `netclaw provider list` | Show configured entries and their types |
| `netclaw provider add <name> <type> [...flags]` | Add a new entry |
| `netclaw provider remove <name>` | Delete an entry |
| `netclaw provider rename <old> <new>` | Rename without re-authenticating |
| `netclaw provider` (no args) | Interactive TUI for add/edit/delete |

### Supported provider types

| Type | Auth | Notes |
|------|------|-------|
| `ollama` | Endpoint only | `--endpoint http://host:11434` |
| `openai` | API key **or** OAuth (ChatGPT sub) | Codex backend for OAuth path |
| `openai-compatible` | Endpoint; optional API key | Generic OpenAI-shape proxies, llama.cpp, vLLM. Also DwarfStar (ds4): `--endpoint http://127.0.0.1:8000`, run `ds4-server` separately, model ids `deepseek-v4-flash` / `deepseek-v4-pro`, context window auto-detected |
| `anthropic` | API key | `sk-ant-...` |
| `openrouter` | API key | `sk-or-...` |
| `github-copilot` | OAuth device flow only | Requires active Copilot subscription on the GitHub account |
| `veniceai` | API key | OpenAI-compatible at `https://api.venice.ai/api/v1`. Suppresses Venice's prepended system prompt by default; opt in via `VendorOptions.IncludeVeniceSystemPrompt = true` |

Provider-specific behavior toggles belong under
`Providers.<name>.VendorOptions`. Netclaw keeps that bag opaque at the core
config layer; each provider plugin deserializes and validates its own typed
options instead of adding provider-specific properties to `ProviderEntry`.

For OpenAI ChatGPT subscription auth, Netclaw persists the OAuth access token,
refresh token, and ChatGPT account ID returned by the OpenAI ID token. The
account ID is required by the Codex backend. If OpenAI OAuth validation reports
that the account ID is missing, re-authenticate the provider with `netclaw
provider fix <name>` or remove and add it again. API-key OpenAI auth does not
use the Codex backend or this account-ID metadata.

For `openai` OAuth providers, `netclaw model discover <provider>` queries the
Codex backend model catalog with the OAuth bearer token and
`ChatGPT-Account-Id`. This path is fail-closed: if the live catalog is
unavailable, returns no picker-visible models, or omits context-window or
input-modality metadata, Netclaw reports the provider error instead of using a
stale built-in model list. The catalog query's `client_version` tracks the
official `@openai/codex` release version, not Netclaw's own version, because the
Codex backend uses that value to gate newer model entries.

When adding an OpenAI provider from the CLI, `netclaw provider add <name>
openai` defaults to the ChatGPT OAuth device flow. Use `--auth api-key
--api-key <key>` to force platform API-key auth instead.

### Adding GitHub Copilot

GitHub Copilot uses the OAuth device flow only — no API key. The operator
must have an active personal Copilot subscription. From the CLI:

```bash
netclaw provider add my-copilot github-copilot --auth oauth-device
```

The terminal prints a user code and the URL `https://github.com/login/device`.
The operator opens the URL in a browser, enters the code, and approves the
Netclaw GitHub App. On success, the long-lived GitHub OAuth token is
persisted to `secrets.json`. A short-lived (~30 min) Copilot API token is
minted lazily on each chat request and never written to disk.

For GitHub Enterprise-backed Copilot, pass the enterprise GitHub host during
setup:

```bash
netclaw provider add my-copilot github-copilot --auth oauth-device --github-host https://github.example.com
```

If the API base cannot be derived from the host, also pass
`--github-api-base`. Netclaw stores the resolved non-secret values as
`Providers.<name>.VendorOptions.GitHubHost` and `.GitHubApiBase`. Runtime
uses those persisted values only; ambient `GH_HOST`, `GITHUB_API_URL`, and
related GitHub environment variables are setup conveniences, not runtime
fallbacks. The Copilot chat/model API base remains the provider `Endpoint`.

If a Copilot probe or chat call returns "GitHub Copilot authorization
expired", the stored OAuth token has been revoked. The remediation is:

```bash
netclaw provider remove my-copilot
netclaw provider add my-copilot github-copilot --auth oauth-device
```

The token is **not** auto-cleared on 401 — the operator retains visibility
into the failing credential until they explicitly remove the entry.

## Search Providers


The `web_search` and `web_fetch` tools route through one configured search
backend, selected by `Search.Backend` in `netclaw.json`:

| Backend | Shape | Notes |
|---------|-------|-------|
| `SearXng` | Self-hosted | Operator runs the instance; `Search.SearXngEndpoint` points at it. JSON output must be enabled in the instance's `settings.yml`. Authenticated instances are not supported in current releases. |
| `Brave`   | Managed | Requires `Search.BraveApiKey` in `secrets.json`. |
| `DuckDuckGo` | Scraped | No config; least reliable, may hit bot detection. |

When a search tool returns an error mentioning `settings.yml`,
`search.formats`, or "rate limit exceeded", the operator's SearXNG instance
is misconfigured or being throttled. Point them at the canonical setup
guide:

```
https://netclaw.dev/docs/configuration/search-providers/
```

That page lists the supported `settings.yml` keys, reverse-proxy header
requirements (Netclaw's outbound `User-Agent` is `Netclaw/{version}
(+https://netclaw.dev)` — non-empty UAs must pass), and the limiter
behavior we honor (HTTP 429 + `Retry-After`).

For Brave, an authentication error surfaces as "API authentication failed"
— the fix is updating `Search.BraveApiKey` in `secrets.json`.
