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
| `deepseek` | API key | DeepSeek hosted API at `https://api.deepseek.com/v1`. Current model ids: `deepseek-v4-flash` and `deepseek-v4-pro` |

Provider-specific behavior toggles belong under
`Providers.<name>.VendorOptions`. Netclaw keeps that bag opaque at the core
config layer; each provider plugin deserializes and validates its own typed
options instead of adding provider-specific properties to `ProviderEntry`.

### Degraded mode: No-Op chat client

When Netclaw starts without an explicitly configured main model/provider (no
`Models` configuration, no `Providers`, or the selected definition points to a
provider that is not configured), the daemon launches in **degraded mode** with
a No-Op chat client. Bound defaults such as `local-ollama/qwen3:30b` do not
count as operator configuration unless those fields are actually present in
config. Every chat turn returns a fixed configuration banner beginning with
`"No valid model configuration detected."` and listing recovery steps. If no
provider is configured, send the operator through `netclaw init`; it configures
both a provider and main model. If a provider already exists but the main model
is missing or points to the wrong provider name, use `netclaw model`. Manual
repair means editing `netclaw.json` / `secrets.json` and restarting the daemon.

A role that names a definition absent from `Models.Definitions` is malformed
configuration, not degraded mode. `netclaw model list` and `netclaw doctor`
report the exact role and missing definition. Repair the role manually or use
`netclaw model set`; `netclaw doctor --fix` does not guess a replacement.

If the operator reports seeing that banner, do not troubleshoot model behavior;
the daemon has no working provider. Direct them through the recovery steps and
restart the daemon after the provider/model config is fixed. `netclaw doctor`
reports the state as a warn-level "Chat Client" item.

Malformed provider configuration, such as a declared provider missing required
credentials, missing provider `Type`, schema-invalid config, or invalid explicit
`Fallback` / `Compaction` model references, is not degraded mode; it fails
startup loudly.

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

### Assigning models to roles and overriding metadata

`netclaw model set <role> <provider> <model-id>` creates or reuses a named model
definition and assigns it to a role (`main`, `fallback`, `compaction`). Definitions
own provider/model identity and metadata, while roles only reference definitions.
Switching away from a model and back therefore preserves its overrides. The operator
owns the context window and modality overrides.

Provider discovery validates the model ID for selection. It does not persist the
discovered context window or modalities. The daemon detects these values at each
startup. Only explicit operator flags or manual configuration create capability
overrides.

Older releases can contain discovery snapshots in the override fields. The config does
not record each field's source. Netclaw preserves these values to protect explicit
operator overrides. Run `netclaw model set` with `--clear-context-window` and
`--clear-modalities` once to restore runtime detection for an affected definition.

- `--context-window <tokens>` clamps the session budget and takes precedence
  over provider-reported detection. Supplying it configures the model manually
  and skips the metadata probe.
- `--input-modalities <list>` / `--output-modalities <list>` override detected
  modalities with a comma-separated list of named flags (`Text`, `Image`,
  `Audio`, `Video`). These do **not** skip the probe. The probe still validates
  the model ID. The override wins over runtime detection.
- `--clear-context-window` and `--clear-modalities` remove the respective
  override so runtime capability detection resolves it again (use these after a
  provider enlarges a model's window or fixes mis-reported modalities).

To change a preserved value you must pass the corresponding flag (a plain
re-set will not touch it). A legacy or hand-edited entry with an unreadable
value does not block a re-set — `model set` migrates legacy inline roles to named
definitions and repairs the selected entry while keeping the fields it can
still read. `model list` reports an unparseable config instead of crashing.
`netclaw doctor --fix` applies only repairs it can derive safely; it does not
invent missing named definitions or role assignments.

### Session input compatibility errors

A saved session can contain image, audio, or video input from an earlier model.
Netclaw checks the complete active history before each model call. If the new
main model lacks a required modality, the turn stops before any provider or
fallback call.

The error names the unsupported modalities and the active model. Select a model
that accepts those modalities, or start a new conversation. Do not diagnose
this result as a provider outage. Netclaw also rejects an unknown saved modality
value instead of omitting that media.

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
