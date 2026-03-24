# netclaw-onboarding Delta Spec

## MODIFIED Requirements

### Requirement: Wizard step order

The wizard step order SHALL be: Provider, ChatServices, SecurityPosture,
ACL, Channels, Search, BrowserAutomation, Identity, HealthCheck.

#### Scenario: Forward navigation through all steps

- **WHEN** the user completes each step sequentially
- **THEN** the steps appear in the order: Provider → ChatServices →
  SecurityPosture → ACL → Channels → Search → BrowserAutomation →
  Identity → HealthCheck

#### Scenario: Slack disabled skips ACL and Channels

- **GIVEN** Slack is disabled in the ChatServices step
- **WHEN** the wizard advances past SecurityPosture
- **THEN** ACL and Channels steps are skipped
- **AND** the wizard proceeds directly to Search

#### Scenario: Slack enabled shows all steps

- **GIVEN** Slack is enabled with a valid bot token
- **WHEN** the wizard advances past SecurityPosture
- **THEN** ACL and Channels steps are shown

### Requirement: ACL uses Slack user search

The ACL step SHALL present a type-to-filter search for Slack users instead
of requiring raw user IDs. The search uses `users.list` via the bot token
validated in ChatServices.

#### Scenario: Owner search by display name

- **GIVEN** the bot token has `users:read` scope
- **WHEN** the user types "aaron" in the owner search
- **THEN** a filtered list shows matching Slack users with display name and ID
- **AND** selecting a user sets the owner identity to their internal Slack ID

#### Scenario: Block on missing users:read scope

- **GIVEN** the bot token lacks `users:read` scope
- **WHEN** the ACL step loads
- **THEN** an error message is shown: "Failed to list users: missing
  users:read scope. Add this scope to your Slack app and press Enter
  to retry."
- **AND** the user cannot advance until the API call succeeds or they
  press Esc to go back and fix credentials

#### Scenario: Block on users.list API failure

- **GIVEN** the bot token has `users:read` scope but `users.list` fails
- **WHEN** the ACL step loads
- **THEN** an error message is shown with the failure reason
- **AND** Enter retries the API call
- **AND** Esc goes back to the previous step

## REMOVED Requirements

### Requirement: Exposure mode step

The Exposure step (Local only / Tailscale / Cloudflare Tunnel) is removed.
Deployment posture is now selected explicitly in the SecurityPosture step.

**Reason:** The exposure mode was only used to infer posture. Explicit posture
selection is clearer and more direct.

**Migration:** Webhook URL collection moves to a sub-step in Identity or
HealthCheck.
