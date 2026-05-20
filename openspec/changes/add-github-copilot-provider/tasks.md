## 1. Shared device-flow fix

- [ ] 1.1 Add `Accept: application/json` header to both
  `StartDeviceAuthorizationAsync` and `PollForTokenAsync` in
  `src/Netclaw.Providers/OAuth/OAuthDeviceFlowService.cs` (switch from
  `HttpClient.PostAsync` to `HttpRequestMessage` so the header can be set)
- [ ] 1.2 Add regression test in
  `src/Netclaw.Configuration.Tests/Providers/OAuth/OAuthDeviceFlowServiceTests.cs`
  covering a mock GitHub-style response that returns 200 with
  `Content-Type: application/x-www-form-urlencoded` when no Accept header is
  sent, and JSON when `Accept: application/json` is sent

## 2. Copilot token exchange

- [ ] 2.1 Create
  `src/Netclaw.Providers/GitHubCopilot/CopilotAuthExpiredException.cs`
  (typed exception carrying the provider entry name)
- [ ] 2.2 Create
  `src/Netclaw.Providers/GitHubCopilot/CopilotTokenExchanger.cs` —
  DI singleton, `ConcurrentDictionary<string, CachedToken>` keyed by
  SHA-256 of OAuth token bytes, 2-minute refresh buffer, `TimeProvider`
  injection, GET `/copilot_internal/v2/token` with
  `Authorization: token <oauth>` / `Accept: application/json` /
  `User-Agent: netclaw/<version>`
- [ ] 2.3 401 from token exchange throws `CopilotAuthExpiredException`;
  any other non-2xx throws an `InvalidOperationException` with the
  endpoint URL and status code in the message
- [ ] 2.4 Unit tests in
  `src/Netclaw.Daemon.Tests/Providers/GitHubCopilot/CopilotTokenExchangerTests.cs`:
  cache hit (no HTTP), cache miss, 2-minute refresh buffer (FakeTimeProvider),
  401 → `CopilotAuthExpiredException`, non-401 error → `InvalidOperationException`

## 3. Copilot request policy

- [ ] 3.1 Create
  `src/Netclaw.Providers/GitHubCopilot/CopilotRequestPolicy.cs` —
  modeled on `OpenAiCodexRequestPolicy`, takes the `CopilotTokenExchanger`
  and `ProviderEntry` in constructor, overrides per-call to set
  `Authorization: Bearer <token>`, `copilot-integration-id: vscode-chat`,
  `editor-version: Netclaw/<version>`, `openai-intent: conversation-agent`
- [ ] 3.2 Verify with a manual smoke that the API accepts
  `editor-version: Netclaw/<version>`; if it rejects, fall back to
  `Neovim/0.6.1` with a code comment citing the failure

## 4. Descriptor

- [ ] 4.1 Create
  `src/Netclaw.Providers/GitHubCopilot/GitHubCopilotDescriptor.cs` —
  `TypeKey = "github-copilot"`, `DisplayName = "GitHub Copilot"`,
  `DefaultEndpoint = "https://api.githubcopilot.com"`,
  `ModelListingPath = "/models"`, `Auth = new OAuthAuth { ... }` with the
  GitHub device-flow endpoints and `Scope = "read:user"`
- [ ] 4.2 Implement `ProbeAsync` — token-exchange via
  `CopilotTokenExchanger`, then GET `/models` with Bearer + the three
  custom headers, filter by `capabilities.type == "chat"` and
  `model_picker_enabled != false`, fall back to the curated list on any
  non-2xx or exception
- [ ] 4.3 Add `CuratedModels` static array with the current known set
  (`gpt-4o`, `gpt-4o-mini`, `gpt-5`, `gpt-5-mini`, `claude-sonnet-4`,
  `o3-mini`) using `ModelModality.Text` defaults until Copilot exposes
  modality metadata
- [ ] 4.4 Unit tests in
  `src/Netclaw.Daemon.Tests/Providers/GitHubCopilot/GitHubCopilotDescriptorTests.cs`:
  probe success parses + filters, probe falls back on HTTP failure, probe
  surfaces auth-expired clearly on 401 from token exchange

## 5. Plugin

- [ ] 5.1 Create
  `src/Netclaw.Providers/GitHubCopilot/GitHubCopilotProviderPlugin.cs` —
  extends `ProviderPluginBase<GitHubCopilotDescriptor>`, ctor takes
  descriptor and `CopilotTokenExchanger`
- [ ] 5.2 `CreateChatClient(entry, model)` builds an `OpenAIClientOptions`
  pointed at `api.githubcopilot.com`, adds the `CopilotRequestPolicy`,
  constructs `new OpenAI.Chat.ChatClient(model.ModelId, new ApiKeyCredential("placeholder"), options)`
  and returns `.AsIChatClient()`

## 6. DI wiring

- [ ] 6.1 Update `src/Netclaw.Providers/ProviderDescriptorCatalog.cs` —
  add `GitHubCopilot` property, include in `All` array, update `Create()`
  factory
- [ ] 6.2 Update
  `src/Netclaw.Providers/ProviderDescriptorServiceExtensions.cs` to
  register the new descriptor (mirror existing pattern)
- [ ] 6.3 Update `src/Netclaw.Providers/LlmProviderServiceExtensions.cs`
  to register `CopilotTokenExchanger` as singleton, register
  `GitHubCopilotProviderPlugin`, and add to the `ILlmProviderPlugin`
  collection

## 7. System skill sync

- [ ] 7.1 Edit
  `feeds/skills/.system/files/netclaw-operations/SKILL.md` — add the
  `github-copilot` provider type to the listing and document the
  `netclaw provider add <name> github-copilot --auth oauth-device` flow
- [ ] 7.2 Bump `metadata.version` in the skill's YAML frontmatter (do NOT
  run `generate-skill-manifest.sh` locally — CI handles publishing per
  CLAUDE.md)

## 8. Quality gates and verification

- [ ] 8.1 `dotnet build Netclaw.slnx` clean
- [ ] 8.2 `dotnet test src/Netclaw.Daemon.Tests/Netclaw.Daemon.Tests.csproj --filter "FullyQualifiedName~GitHubCopilot"` passes
- [ ] 8.3 `dotnet test src/Netclaw.Configuration.Tests/Netclaw.Configuration.Tests.csproj --filter "FullyQualifiedName~OAuthDeviceFlow"` passes
- [ ] 8.4 `dotnet slopwatch analyze` — no new violations
- [ ] 8.5 `./scripts/Add-FileHeaders.ps1 -Verify` — all new `.cs` files
  carry the Petabridge copyright header
- [ ] 8.6 `./scripts/smoke/run-smoke.sh light` —
  TUI provider picker shows "GitHub Copilot" without breaking existing tapes
- [ ] 8.7 Manual end-to-end smoke against a real Copilot subscription:
  `netclaw provider add copilot-personal github-copilot --auth oauth-device`
  then re-run the same `provider add` command to confirm the cached
  Copilot API token round-trips, plus a one-shot chat through the new
  provider entry. (No `netclaw provider probe` subcommand exists today;
  the add flow exercises the probe path internally.)
