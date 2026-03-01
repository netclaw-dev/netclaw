## Context

`WebSearchTool` in `Netclaw.Actors` is a monolithic class that hardcodes
DuckDuckGo Lite HTML scraping, including user-agent randomization, rate
limiting, and CAPTCHA detection. The spec calls for configurable backends
(Brave Search, SearXNG) but no abstraction layer exists. The tool's interface
to the agent (`web_search(query, max_results?)` returning title/URL/snippet)
is stable and SHALL remain unchanged.

Current dependency chain:

```
Netclaw.Actors → HtmlAgilityPack (for DDG HTML parsing)
Netclaw.Configuration → ToolConfig (shell timeout, max output chars only)
```

## Goals / Non-Goals

**Goals:**

- Introduce `ISearchBackend` abstraction so the agent-facing `WebSearchTool`
  delegates to a swappable backend selected at startup via DI.
- Ship three backends: DuckDuckGo (migrated), Brave Search (new), SearXNG (new).
- DuckDuckGo is the default backend — zero configuration required.
- Add `SearchConfig` to `Netclaw.Configuration` for backend selection and
  credentials.
- Keep `web_search` tool interface identical to the agent — same name, same
  params, same output format.

**Non-Goals:**

- Init wizard changes (deferred — search works out of the box with DDG).
- Runtime backend switching (backend is selected at startup, not per-request).
- Search result caching or deduplication across backends.
- Custom search engines beyond the three named backends.

## Decisions

### Decision 1: New `Netclaw.Search` project for backend implementations

**Choice:** Create `Netclaw.Search` with `ISearchBackend`, `SearchResult`,
and all three backend implementations.

**Alternatives considered:**
- Keep backends in `Netclaw.Actors` behind an interface — rejected because
  HtmlAgilityPack dependency would bleed into the actor project unnecessarily,
  and the search concern is distinct from actor orchestration.
- Put interface in `Netclaw.Tools.Abstractions` — rejected because search is
  not a tool abstraction; it's a domain concern consumed by a tool.

**Rationale:** Follows the existing project naming convention (`Netclaw.Channels`,
`Netclaw.Security`). Scopes HtmlAgilityPack to `Netclaw.Search` only. Clean
dependency: `Netclaw.Search` → `Netclaw.Configuration`.

### Decision 2: `SearchConfig` lives in `Netclaw.Configuration`

**Choice:** Add `SearchConfig` class to `Netclaw.Configuration` alongside
existing config types (`ToolConfig`, `SlackConfig`, etc.).

**Rationale:** All config shapes live in `Netclaw.Configuration` so the wizard,
daemon, and CLI can reference them without depending on implementation projects.

Config shape:

```csharp
public sealed class SearchConfig
{
    public string Backend { get; set; } = "duckduckgo";
    public string? BraveApiKey { get; set; }
    public string? SearXngEndpoint { get; set; }
}
```

In `netclaw.json`:
```json
{ "Search": { "Backend": "brave" } }
```

In `secrets.json`:
```json
{ "Search": { "BraveApiKey": "BSA-xxx..." } }
```

### Decision 3: DuckDuckGo as default backend

**Choice:** DuckDuckGo is the default when no search config is present.

**Alternatives considered:**
- Brave as default (current spec) — rejected because it requires an API key,
  violating zero-config first-run UX.
- No default (require explicit config) — rejected because search should work
  out of the box.

**Rationale:** A fresh `netclaw init` should produce a working agent without
touching search config. Users upgrade to Brave/SearXNG when they hit DDG's
bot detection limits.

### Decision 4: Backend error semantics

**Choice:** `ISearchBackend.SearchAsync` returns `SearchBackendResult` — a
discriminated result type that separates success (results list) from backend
errors (message string). This lets `WebSearchTool` give the agent actionable
error messages (e.g., "DuckDuckGo blocked by CAPTCHA" vs "Brave API key
invalid" vs "SearXNG unreachable").

**Alternatives considered:**
- Throw exceptions — rejected because search failures are expected operational
  events (DDG CAPTCHAs), not exceptional conditions.
- Return empty list — rejected because the agent can't distinguish "no results"
  from "backend broken."

### Decision 5: `WebSearchTool` becomes a thin delegate

**Choice:** `WebSearchTool` constructor takes `ISearchBackend`. It validates
params, calls `_backend.SearchAsync(...)`, and formats the result. All
DDG-specific logic (HTML parsing, UA spoofing, rate limiting) moves to
`DuckDuckGoBackend`.

The `SearchResult` record moves to `Netclaw.Search` as a shared type.

### Decision 6: DI wiring in daemon

**Choice:** The daemon's DI setup reads `SearchConfig.Backend` and registers
the corresponding `ISearchBackend` implementation. Unknown backend values log
a warning and fall back to DuckDuckGo rather than crashing startup.

```
SearchConfig.Backend → "duckduckgo" → DuckDuckGoBackend
                     → "brave"      → BraveSearchBackend (requires BraveApiKey)
                     → "searxng"    → SearXngBackend (requires SearXngEndpoint)
```

If Brave is selected but no API key is configured, the web search tool is not
registered and a warning is logged (fail-closed for missing credentials,
consistent with spec).

## Risks / Trade-offs

**[Risk] DuckDuckGo bot detection degrades over time** → Mitigation: DDG is
the fallback default, not the recommended backend. The CAPTCHA error message
now points users to configure Brave or SearXNG as alternatives.

**[Risk] Brave Search free tier rate limits** → Mitigation: Free tier allows
2,000 queries/month. For a homelab assistant this is likely sufficient. Paid
tier is $5/1,000 requests. Rate limiting is not in scope for this change.

**[Risk] SearXNG JSON format disabled by default** → Mitigation: The
`SearXngBackend` validates the response content type. If JSON is not enabled,
it returns a clear error message telling the user to enable `json` in their
SearXNG `settings.yml`.

**[Trade-off] No init wizard step for search** → Accepted for this change.
Search works out of the box with DDG. Adding a wizard step is a follow-up
concern in `netclaw-onboarding` once the backend abstraction is stable.

## Open Questions

- Should the health check in the init wizard validate search backend
  connectivity? Deferred to a follow-up change since DDG default needs no
  validation.
