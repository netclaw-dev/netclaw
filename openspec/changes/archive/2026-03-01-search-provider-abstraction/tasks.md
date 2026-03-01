## 1. Project scaffolding and shared types

- [x] 1.1 Create `Netclaw.Search` project (net10.0, reference `Netclaw.Configuration`)
- [x] 1.2 Create `Netclaw.Search.Tests` project (reference `Netclaw.Search`, xUnit)
- [x] 1.3 Add `SearchConfig` to `Netclaw.Configuration` (Backend string defaulting to "duckduckgo", nullable BraveApiKey, nullable SearXngEndpoint)
- [x] 1.4 Define `SearchResult` record in `Netclaw.Search` (Title, Url, Snippet)
- [x] 1.5 Define `SearchBackendResult` discriminated result type (success with results list, error with message)
- [x] 1.6 Define `ISearchBackend` interface (`Task<SearchBackendResult> SearchAsync(string query, int maxResults, CancellationToken ct)`)

## 2. DuckDuckGo backend (migrate existing logic)

- [x] 2.1 Create `DuckDuckGoBackend` in `Netclaw.Search` implementing `ISearchBackend`
- [x] 2.2 Move HTML parsing logic from `WebSearchTool.ParseResults` to `DuckDuckGoBackend`
- [x] 2.3 Move rate limiting, UA randomization, and CAPTCHA detection to `DuckDuckGoBackend`
- [x] 2.4 Move HtmlAgilityPack package reference from `Netclaw.Actors` to `Netclaw.Search`
- [x] 2.5 Migrate DDG HTML fixture tests from `Netclaw.Actors.Tests` to `Netclaw.Search.Tests`
- [x] 2.6 Update CAPTCHA error message to suggest Brave Search or SearXNG as alternatives

## 3. Brave Search backend

- [x] 3.1 Create `BraveSearchBackend` in `Netclaw.Search` implementing `ISearchBackend`
- [x] 3.2 Implement HTTP GET to `https://api.search.brave.com/res/v1/web/search` with `X-Subscription-Token` header
- [x] 3.3 Parse Brave Search JSON response into `SearchResult` records
- [x] 3.4 Handle 401 (invalid key) and 429 (rate limit) responses as error results
- [x] 3.5 Add fixture-based tests for Brave Search JSON parsing
- [x] 3.6 Add error handling tests (auth failure, rate limit, network error)

## 4. SearXNG backend

- [x] 4.1 Create `SearXngBackend` in `Netclaw.Search` implementing `ISearchBackend`
- [x] 4.2 Implement HTTP GET to `{endpoint}/search?q={query}&format=json`
- [x] 4.3 Parse SearXNG JSON response into `SearchResult` records
- [x] 4.4 Detect non-JSON responses and return error indicating JSON format must be enabled
- [x] 4.5 Handle unreachable endpoint as error result
- [x] 4.6 Add fixture-based tests for SearXNG JSON parsing
- [x] 4.7 Add error handling tests (unreachable, non-JSON response)

## 5. Refactor WebSearchTool to delegate

- [x] 5.1 Add `Netclaw.Search` reference to `Netclaw.Actors` project
- [x] 5.2 Change `WebSearchTool` constructor to accept `ISearchBackend`
- [x] 5.3 Replace inline DDG logic with `_backend.SearchAsync(...)` delegation
- [x] 5.4 Map `SearchBackendResult` error case to tool error string for the agent
- [x] 5.5 Remove DDG-specific code from `WebSearchTool` (HTML parsing, UA headers, rate limiting)
- [x] 5.6 Remove HtmlAgilityPack reference from `Netclaw.Actors.csproj`
- [x] 5.7 Update `WebSearchTool` tests in `Netclaw.Actors.Tests` (mock `ISearchBackend`)

## 6. DI wiring and configuration

- [x] 6.1 Register `SearchConfig` binding in daemon startup (from `netclaw.json` + `secrets.json`)
- [x] 6.2 Add backend factory logic: read `SearchConfig.Backend`, register matching `ISearchBackend`
- [x] 6.3 Handle missing Brave API key: skip web search tool registration, log warning
- [x] 6.4 Handle missing SearXNG endpoint: skip web search tool registration, log warning
- [x] 6.5 Handle unknown backend value: log warning, fall back to DuckDuckGo
- [x] 6.6 Update `ToolRegistrationExtensions.WithFirstPartyTools` to accept `ISearchBackend`

## 7. Verification

- [x] 7.1 All existing tests pass (`dotnet test`)
- [x] 7.2 Run `dotnet slopwatch analyze` — no new violations
- [x] 7.3 Verify DDG backend works end-to-end with no config (default path)
- [x] 7.4 Verify Brave backend works with API key configured
- [x] 7.5 Verify SearXNG backend works with endpoint configured
