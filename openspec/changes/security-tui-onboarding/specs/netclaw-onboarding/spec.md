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

### Requirement: ACL uses Slack user search

The ACL step SHALL present a type-to-filter search for Slack users instead
of requiring raw user IDs. The search uses `users.list` via the bot token
validated in ChatServices.

#### Scenario: Owner search by display name

- **GIVEN** the bot token has `users:read` scope
- **WHEN** the user types "aaron" in the owner search
- **THEN** a filtered list shows matching Slack users with display name and ID
- **AND** selecting a user sets the owner identity to their internal Slack ID

#### Scenario: Fallback to manual ID entry

- **GIVEN** the bot token lacks `users:read` scope
- **WHEN** the ACL step loads
- **THEN** a text input for manual user ID entry is shown
- **AND** a message explains that user search requires the `users:read` scope

## REMOVED Requirements

### Requirement: Exposure mode step

The Exposure step (Local only / Tailscale / Cloudflare Tunnel) is removed.
Deployment posture is now selected explicitly in the SecurityPosture step.

**Reason:** The exposure mode was only used to infer posture. Explicit posture
selection is clearer and more direct.

**Migration:** Webhook URL collection moves to a sub-step in Identity or
HealthCheck.
