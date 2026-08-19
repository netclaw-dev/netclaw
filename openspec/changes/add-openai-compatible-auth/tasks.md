## 1. Auth contract

- [x] 1.1 Add `EndpointOrApiKeyAuth : IProviderAuth` in
  `src/Netclaw.Providers/IProviderAuth.cs` with
  `SupportedAuthMethods = [AuthMethod.None, AuthMethod.ApiKey]`.
- [x] 1.2 Change `OpenAiCompatibleDescriptor.Auth` from `EndpointOnlyAuth`
  to `EndpointOrApiKeyAuth`.
- [x] 1.3 Confirmed no transport change is needed: chat client
  (`OpenAiCompatibleChatClient.BuildRequest`), models client, capability
  resolver, and descriptor probe send Bearer when a key exists and no header
  when it does not. Untouched by this change.

## 2. Init wizard

- [x] 2.1 `OAuthFlowViews.BuildAuthMethodLabels`: `AuthMethod.None` renders
  as "No auth (local endpoint)" and is included only for multi-method
  providers; single-`None` providers (Ollama) still bypass the picker.
  `ParseAuthMethodLabel` round-trips the new label.
- [x] 2.2 `ProviderStepView.BuildCredentialInput`: `EndpointOrApiKeyAuth`
  branch — "No auth" shows endpoint input (sub-step 2 → probe); "API Key"
  shows endpoint input then a new sub-step 10 for the key input. Back
  navigation from 10 returns to 2.
- [x] 2.3 `ProviderStepViewModel`: `BuildProbeEntry` already carries
  `ApiKey` when set (no change); `ContributeConfig`/`BuildProviderEntry`
  now default the endpoint from descriptors of shape `EndpointOnlyAuth or
  EndpointOrApiKeyAuth`; `WriteProviderCredentials` persists the selected
  method and encrypted key via the existing `ProviderCredentialWriter`.
- [x] 2.4 `ChatClientDoctorCheck.MissingCredentialMessage`: an entry that
  declares `AuthMethod: ApiKey` with no stored key now fails with guidance
  (previously any provider supporting `None` skipped all credential checks).

## 3. Provider manager TUI

- [x] 3.1 `AdvanceAfterName` already routes multi-method providers to the
  auth picker (no change needed); verified by test.
- [x] 3.2 `BuildAddAuthView` renders both labels via the shared
  `BuildAuthMethodLabels` (covered by 2.1).
- [x] 3.3 New `AddCredentialsEndpoint` state + `BuildCredentialsEndpointView`
  (endpoint stage before key stage for the ApiKey path);
  `BuildCredentialsView` handles `EndpointOrApiKeyAuth` for both methods;
  new `FixApiKey` state + `BuildFixApiKeyView` for repairing a key'd entry
  (endpoint stage first, then key stage, via `SubmitFixEndpoint`).
- [x] 3.4 `WriteProviderConfig` persists via `ProviderCredentialWriter` with
  the selected `NewAuthMethod` (no change needed); `SubmitFixCredentials`
  key-required guard corrected to require a key only when the type is
  key-only OR the entry declares `AuthMethod.ApiKey` (fixes a latent
  regression where a no-auth openai-compatible entry would have demanded a
  key).

## 4. Tests

- [x] 4.1 `OpenAiCompatibleAuthTests.OpenAiCompatible_Auth_SupportsNoneAndApiKeyInOrder`.
- [x] 4.2 Manager VM transitions: None → `AddCredentials`; ApiKey →
  `AddCredentialsEndpoint` → key stage; empty-key ApiKey submit blocks.
- [x] 4.3 Fake-failure gates: `SubmitCredentials_...EmptyKey_BlocksBeforeProbe`
  (no probe, no config write) and
  `SubmitFixCredentials_...ApiKeyEntryWithEmptyKey_Blocks`.
- [x] 4.4 Wizard equivalents: probe entry carries key / no key;
  `ContributeConfig` both methods; `WriteProviderCredentials` both methods
  (encrypted secret asserted via `ENC:` prefix).
- [x] 4.5 Headless typed-key end-to-end:
  `ManagerAddFlow_OpenAiCompatibleApiKey_TypedKeyEndToEnd` drives the real
  page through type list → name → auth picker → endpoint → key → AddComplete
  and asserts the persisted config. No `Thread.Sleep`/`Task.Delay` in
  orchestration (polling via `Task.Yield` + cancellation).
- [x] 4.6 Doctor:
  `ReturnsError_WhenOpenAiCompatibleDeclaresApiKeyWithoutStoredKey`,
  `ReturnsPass_WhenOpenAiCompatibleUsesNoAuth`.

## 5. Operator guidance

- [x] 5.1 `feeds/skills/.system/files/netclaw-operations/references/providers.md`:
  `openai-compatible` row documents Bearer, `--api-key`, the TUI choice, and
  the doctor report for a declared-ApiKey-without-key entry.
- [x] 5.2 Skill version bumped 2.56.0 → 2.57.0.

## 7. Quality gates

- [x] 7.1 `dotnet build` clean (0 warnings). Netclaw.Cli.Tests 1389/1391
  (2 pre-existing environment skips); Netclaw.Daemon.Tests 1047/1047;
  Netclaw.Configuration.Tests 604/604.
- [x] 7.2 `dotnet slopwatch analyze` — one pre-existing SW004 warning in
  `PowerShellHostProbeTests.cs` (outside this diff, documented in a prior
  change). No new violations.
- [x] 7.3 `./scripts/Add-FileHeaders.ps1 -Verify` — all files have headers.
