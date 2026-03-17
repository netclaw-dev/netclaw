## MODIFIED Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`. The onboarding
wizard SHALL collect Slack credentials, provider configuration, ACL inputs,
search backend, browser automation, memory provider selection, MCP server
configuration, and exposure mode selection. On completion, the wizard SHALL
run a health check to verify the baseline configuration is functional.

For providers that support `OAuthPkce`, the wizard SHALL present a browser-based OAuth flow with three fallback layers:
1. Automatic browser launch via `Process.Start` with the authorization URL.
2. If browser launch fails, display the authorization URL using `CopyableTextNode` with OSC 52 clipboard auto-copy and `ToastOverlayNode` confirmation.
3. If the localhost callback cannot be received, display a `TextInputNode` where the operator can paste the redirect URL containing the authorization code.

#### Scenario: First-time setup

- **WHEN** operator runs `netclaw init` on a fresh install
- **THEN** guided setup collects provider, Slack, ACL, search, browser
  automation, memory, and exposure mode inputs
- **AND** writes a runnable baseline configuration

#### Scenario: Browser OAuth in init wizard

- **WHEN** operator selects a provider with `OAuthPkce` support during init
- **AND** chooses the OAuth auth method
- **THEN** the wizard attempts to open the authorization URL in the default browser
- **AND** displays a spinner while waiting for the callback
- **AND** on callback success, transitions to probe validation

#### Scenario: Browser fails to open during init

- **WHEN** the browser cannot be opened automatically
- **THEN** the wizard displays the authorization URL via `CopyableTextNode`
- **AND** attempts OSC 52 clipboard copy of the URL
- **AND** shows toast confirmation if clipboard write was emitted
- **AND** continues waiting for the callback

#### Scenario: Callback unreachable during init

- **WHEN** the operator cannot receive the localhost callback
- **THEN** the wizard displays a `TextInputNode` labeled "Paste the redirect URL"
- **AND** on valid URL paste, extracts the authorization code and completes the flow
- **AND** on invalid URL paste, shows an inline error and allows retry
