## ADDED Requirements

### Requirement: Search backend abstraction

The system SHALL define an `ISearchBackend` interface that all search
providers implement. The interface SHALL accept a query string, maximum result
count, and cancellation token, and SHALL return a result type that
distinguishes successful results from backend errors.

#### Scenario: Backend returns search results

- **GIVEN** a configured search backend
- **WHEN** `SearchAsync` is called with a query and max result count
- **THEN** the backend returns a list of `SearchResult` records (title, URL,
  snippet)

#### Scenario: Backend returns error

- **GIVEN** a configured search backend that encounters a failure
- **WHEN** `SearchAsync` is called
- **THEN** the backend returns an error result with a human-readable message
- **AND** the error does not throw an exception

### Requirement: DuckDuckGo search backend

The system SHALL provide a `DuckDuckGoBackend` that searches via DuckDuckGo
Lite HTML scraping. The backend SHALL use randomized user-agent headers and
rate limiting to reduce bot detection. The backend SHALL detect CAPTCHA
responses and return an error result suggesting alternative backends.

#### Scenario: Successful DuckDuckGo search

- **GIVEN** the DuckDuckGo backend is configured
- **WHEN** a search query is submitted
- **THEN** the backend sends an HTTP request to DuckDuckGo Lite
- **AND** parses HTML results into `SearchResult` records
- **AND** returns up to the requested maximum number of results

#### Scenario: DuckDuckGo bot detection triggered

- **GIVEN** the DuckDuckGo backend is configured
- **WHEN** DuckDuckGo returns a CAPTCHA/bot detection page
- **THEN** the backend returns an error result indicating bot detection
- **AND** the error message suggests configuring Brave Search or SearXNG

#### Scenario: Rate limiting between requests

- **GIVEN** multiple search requests are made in rapid succession
- **WHEN** the DuckDuckGo backend processes requests
- **THEN** a randomized delay (500-2000ms) is enforced between requests

### Requirement: Brave Search backend

The system SHALL provide a `BraveSearchBackend` that searches via the Brave
Search API. The backend SHALL authenticate using the `X-Subscription-Token`
header with the configured API key.

#### Scenario: Successful Brave Search query

- **GIVEN** the Brave Search backend is configured with a valid API key
- **WHEN** a search query is submitted
- **THEN** the backend sends a GET request to
  `https://api.search.brave.com/res/v1/web/search`
- **AND** includes the API key in the `X-Subscription-Token` header
- **AND** parses JSON results into `SearchResult` records

#### Scenario: Brave Search authentication failure

- **GIVEN** the Brave Search backend is configured with an invalid API key
- **WHEN** a search query is submitted
- **THEN** the backend returns an error result indicating authentication failure

#### Scenario: Brave Search rate limit exceeded

- **GIVEN** the Brave Search free tier rate limit is exceeded
- **WHEN** a search query is submitted
- **THEN** the backend returns an error result indicating rate limit exceeded

### Requirement: SearXNG search backend

The system SHALL provide a `SearXngBackend` that searches via a self-hosted
SearXNG instance. The backend SHALL send queries to the configured endpoint
with `format=json`.

#### Scenario: Successful SearXNG search

- **GIVEN** the SearXNG backend is configured with a reachable endpoint
- **WHEN** a search query is submitted
- **THEN** the backend sends a GET request to `{endpoint}/search?q={query}&format=json`
- **AND** parses JSON results into `SearchResult` records

#### Scenario: SearXNG endpoint unreachable

- **GIVEN** the SearXNG backend is configured with an unreachable endpoint
- **WHEN** a search query is submitted
- **THEN** the backend returns an error result indicating the endpoint is
  unreachable

#### Scenario: SearXNG JSON format not enabled

- **GIVEN** the SearXNG instance does not have JSON format enabled
- **WHEN** a search query is submitted and the response is not valid JSON
- **THEN** the backend returns an error result indicating JSON format must be
  enabled in SearXNG settings

### Requirement: Search configuration

The system SHALL read search backend configuration from `SearchConfig` in
`Netclaw.Configuration`. The default backend SHALL be `duckduckgo` when no
configuration is present.

#### Scenario: Default backend when unconfigured

- **GIVEN** no search configuration is present in `netclaw.json`
- **WHEN** the daemon starts
- **THEN** the DuckDuckGo backend is used

#### Scenario: Brave backend configured

- **GIVEN** `netclaw.json` specifies `Search.Backend` as `"brave"`
- **AND** `secrets.json` contains `Search.BraveApiKey`
- **WHEN** the daemon starts
- **THEN** the Brave Search backend is registered with the configured API key

#### Scenario: SearXNG backend configured

- **GIVEN** `netclaw.json` specifies `Search.Backend` as `"searxng"`
- **AND** `netclaw.json` contains `Search.SearXngEndpoint`
- **WHEN** the daemon starts
- **THEN** the SearXNG backend is registered with the configured endpoint

#### Scenario: Brave backend without API key

- **GIVEN** `netclaw.json` specifies `Search.Backend` as `"brave"`
- **AND** no API key is configured
- **WHEN** the daemon starts
- **THEN** the web search tool is not registered
- **AND** a warning is logged indicating the Brave API key is missing
