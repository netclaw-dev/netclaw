## Tasks

### 1. Reorder WizardStep enum and navigation

- [x] Update `WizardStep` enum in `InitWizardViewModel.cs`:
      Provider, ChatServices, SecurityPosture, Acl, Channels, Search,
      BrowserAutomation, Identity, HealthCheck
- [x] Remove `Exposure` step enum value
- [x] Add `SecurityPosture` and `Channels` step enum values
- [x] Update `GetDisplayStepNumber()` for new step count
- [x] Update `GoForward()` / `GoBack()` skip logic
- [x] Update step indicator text for new step names
- [x] Verify: forward/back navigation visits all steps in correct order

**Acceptance:** Done.

### 2. Implement SecurityPosture step UI

- [x] Add `_securityPostureList` (`SelectionListNode<string>`) field
- [x] Items: "Personal — Only you on this machine",
      "Team — Shared with trusted teammates", "Public — Open to untrusted users"
- [x] On `SelectionConfirmed`: set `ViewModel.SelectedPosture`, derive shell
      mode and audience defaults, advance to next step
- [x] Add `BuildSecurityPostureStep()` renderer method
- [x] Show explanatory text below the selection
- [x] Add `SelectedPosture` property to `InitWizardViewModel`
- [x] Add `DeriveSecurityDefaults()` that sets shell mode + audience defaults
      based on posture
- [x] Wire into `BuildStepContent()` switch

**Acceptance:** Done.

### 3. Keep ChatServices step as-is

DESCOPED: Channel names and user IDs stay as text inputs in their existing
locations. One-time setup — not worth the Slack API search complexity.
Channel names remain in ChatServices (comma-separated, resolved during
health check). User IDs remain in ACL step (manual paste).

- [x] No changes needed — existing inputs are adequate for one-time setup

**Acceptance:** N/A — descoped.

### 4. Implement Channels step UI

- [x] Add `_channelCursorIndex` for focused row tracking
- [x] Add `ChannelEntries` list to ViewModel with `ChannelEntry` class
- [x] Pre-populate DMs row if DMs enabled, with posture default audience
- [x] Pre-populate channels from `LastChannelResolution` if available
- [x] Build `BuildChannelsStep()` renderer with audience display
- [x] Handle ↑/↓ for row navigation (custom, not SelectionListNode)
- [x] Handle ←/→ for audience cycling on focused row
- [x] Handle `d` key: remove focused channel (not DMs row)
- [x] Handle Enter: advance to next wizard step

**Acceptance:** Done.

### 5. Channel-add sub-step with Slack API search

DESCOPED: Users type channel names in ChatServices step (comma-separated)
as they do today. Resolved via conversations.list during health check.
Not worth building a type-to-filter search UI for one-time setup.

- [x] No changes needed — existing flow is adequate

**Acceptance:** N/A — descoped.

### 6. Slack user search extraction

DESCOPED: Users paste raw Slack user IDs as they do today. Not worth
extracting shared service from LookupSlackUserTool for one-time setup.

- [x] No changes needed — existing flow is adequate

**Acceptance:** N/A — descoped.

### 7. ACL step — keep as manual ID entry

DESCOPED: Owner identity and allowed user IDs remain as manual text input.

- [x] No changes needed — existing flow is adequate

**Acceptance:** N/A — descoped.

### 8. Update config generation

- [x] `WriteConfig()` uses `SelectedPosture` directly
- [x] Shell mode derived from posture in ViewModel
- [x] `ChannelAudiences` written from `ChannelEntries` list via
      `SyncChannelAudiencesFromEntries()`
- [x] Remove `ExposureMode` property and related config writes
- [x] Move webhook URL to Identity sub-step
- [x] Verify: generated config passes `netclaw doctor` schema validation

**Acceptance:** Config generated from explicit user choices, not inferred.

### 9. Move webhook URL collection

- [x] Add webhook URL as optional sub-step in Identity step (after timezone)
- [x] Keep existing `_webhookUrlInput` TextInputNode
- [x] Preserve "press Enter to skip" behavior

**Acceptance:** Webhook URL still collected, just in a different step.

### 10. Tests

**ViewModel unit tests:**
- [x] `DeriveSecurityDefaults()` maps posture to shell mode + audience defaults
- [x] Audience cycling wraps correctly (Team→Personal→Public→Team, reverse too)
- [x] DMs row cannot be removed
- [x] Config generation uses explicit posture (not inferred from exposure)

**Headless TUI integration tests (VirtualTerminal + VirtualInputSource):**
- [x] SecurityPosture step renders posture options, Enter selects and advances
- [x] Channels step renders channel list with audience values
- [x] ←/→ on channel row cycles audience in rendered terminal output
- [x] `d` key removes focused channel
- [x] Full forward navigation through new step order
- [x] Slack-disabled flow skips ACL and Channels steps

**Quality gates:**
- [x] `dotnet slopwatch analyze` — no new violations
- [x] Existing `InitWizardPageTests` still pass (no regression)
