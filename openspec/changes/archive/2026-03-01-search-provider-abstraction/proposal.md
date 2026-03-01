## Why

The current web search tool is hardcoded to DuckDuckGo Lite via HTML scraping.
DDG frequently triggers bot detection (CAPTCHAs), making search unreliable for
an always-on homelab agent. PRD-001 specifies "Brave Search API or equivalent"
as the search backend (line 228). Users need the ability to choose a search
provider that fits their setup — DuckDuckGo for zero-config default, Brave
Search for reliability with a free API key, or SearXNG for self-hosted privacy.

## What Changes

- Extract search backend logic from `WebSearchTool` into a new `Netclaw.Search`
  project with an `ISearchBackend` interface.
- Implement three backends: `DuckDuckGoBackend` (existing logic, migrated),
  `BraveSearchBackend` (new, JSON API), and `SearXngBackend` (new, JSON API).
- Add `SearchConfig` to `Netclaw.Configuration` for backend selection and
  credentials (API key for Brave, endpoint URL for SearXNG).
- Refactor `WebSearchTool` to be a thin delegate — same tool name, same params,
  same output format to the agent. Backend is selected at startup via DI.
- DuckDuckGo is the default backend (no configuration required). Spec currently
  says Brave is default — this change corrects that to match zero-config UX.
- Update `netclaw.json` / `secrets.json` config writing to support search
  backend selection.

## Capabilities

### New Capabilities

- `netclaw-search`: Search provider abstraction, backend implementations
  (DuckDuckGo, Brave Search, SearXNG), and configuration model.

### Modified Capabilities

- `netclaw-tools`: Update configurable search backend requirement to reflect
  DuckDuckGo as default (was Brave) and add DuckDuckGo as a named backend
  option with a bot-detection warning.
- `netclaw-onboarding`: Init wizard gains optional search provider selection
  step with backend-specific validation probes.

## Impact

- **New project**: `Netclaw.Search` — `ISearchBackend`, `SearchResult`,
  three backend implementations.
- **Modified projects**: `Netclaw.Configuration` (add `SearchConfig`),
  `Netclaw.Actors` (thin `WebSearchTool`, remove DDG logic), `Netclaw.Cli`
  (wizard step), `Netclaw.Daemon` (DI wiring).
- **New dependency**: None for Brave/SearXNG (just `HttpClient` + STJ).
  HtmlAgilityPack moves from `Netclaw.Actors` to `Netclaw.Search`.
- **Test migration**: DDG HTML fixture tests move from `Netclaw.Actors.Tests`
  to a new `Netclaw.Search.Tests` project.
- **Config schema**: `netclaw.json` gains a `Search` section; `secrets.json`
  gains `Search.BraveApiKey`.
- **Security**: Brave API key stored in `secrets.json` (same pattern as
  provider API keys). SearXNG endpoint is non-secret config.
- **No breaking changes**: `web_search` tool interface to the agent is unchanged.
