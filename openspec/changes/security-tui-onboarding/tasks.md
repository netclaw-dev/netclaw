## Tasks

### 1. Reorder WizardStep enum and navigation

- [ ] Update `WizardStep` enum in `InitWizardViewModel.cs`:
      Provider, ChatServices, SecurityPosture, Acl, Channels, Search,
      BrowserAutomation, Identity, HealthCheck
- [ ] Remove `Exposure` step enum value
- [ ] Add `SecurityPosture` and `Channels` step enum values
- [ ] Update `GetDisplayStepNumber()` for new step count
- [ ] Update `GoForward()` / `GoBack()` skip logic (Memory still skipped,
      ACL skipped when no chat services, Channels skipped when no Slack)
- [ ] Update step indicator text for new step names
- [ ] Verify: forward/back navigation visits all steps in correct order

**Acceptance:** Wizard navigates through new step order. No steps missing
or duplicated.

### 2. Implement SecurityPosture step UI

- [ ] Add `_securityPostureList` (`SelectionListNode<string>`) field
- [ ] Items: "Personal — Only you on this machine",
      "Team — Shared with trusted teammates", "Public — Open to untrusted users"
- [ ] On `SelectionConfirmed`: set `ViewModel.SelectedPosture`, derive shell
      mode and audience defaults, advance to next step
- [ ] Add `BuildSecurityPostureStep()` renderer method
- [ ] Show explanatory text below the selection
- [ ] Add `SelectedPosture` property to `InitWizardViewModel`
- [ ] Add `DeriveSecurityDefaults()` that sets shell mode + audience defaults
      based on posture
- [ ] Wire into `BuildStepContent()` switch

**Acceptance:** User selects posture, defaults propagate. Back navigation
preserves selection.

### 3. Simplify ChatServices step (remove channel entry)

- [ ] Remove channel name text input from ChatServices (sub-step 3)
- [ ] Remove allowed user IDs text input from ChatServices (sub-step 5)
- [ ] ChatServices becomes: Enable Slack? → Bot token → App token → DMs?
- [ ] Update `_chatServicesSubStep` count and navigation
- [ ] Channel and user management moves to Channels and ACL steps

**Acceptance:** ChatServices collects only Slack credentials and DM preference.

### 4. Implement Channels step UI

- [ ] Add `_channelCursorIndex` for focused row tracking
- [ ] Add `ChannelEntries` list to ViewModel: `List<(string Name, string Id, string Audience)>`
- [ ] Pre-populate DMs row if DMs enabled, with posture default audience
- [ ] Pre-populate channels from `LastChannelResolution` if available
- [ ] Build `BuildChannelsStep()` renderer:
  - Each row: `  {name}  {id}  [◀ {audience} ▶]`
  - Focused row highlighted
  - `[a] Add channel` prompt at bottom
  - Help text explaining audience levels
- [ ] Handle ↑/↓ for row navigation (custom, not SelectionListNode)
- [ ] Handle ←/→ for audience cycling on focused row
- [ ] Handle `a` key: switch to channel-add sub-step
- [ ] Handle `d` key: remove focused channel (not DMs row)
- [ ] Handle Enter: advance to next wizard step
- [ ] Handle Esc: go back to previous step

**Acceptance:** Channels displayed with audience. ←/→ cycles audience. a/d
add/remove channels. Enter advances.

### 5. Implement channel-add sub-step with Slack API

- [ ] Move `conversations.list` call from HealthCheck to Channels step
- [ ] On `a` key: show TextInputNode for channel name filter
- [ ] Filter cached conversation list client-side as user types
- [ ] Show filtered results below input as selectable list
- [ ] Enter on a result: add channel to `ChannelEntries` with posture default
- [ ] Esc: return to channel list without adding
- [ ] Handle duplicate detection (don't add same channel twice)

**Acceptance:** User types to filter channels, selects one, it appears in
the list with default audience.

### 6. Extract shared user listing from LookupSlackUserTool

- [ ] Extract `GetUsersAsync` pagination + filtering + caching logic from
      `LookupSlackUserTool` into a shared service (e.g., `SlackUserListService`
      or similar) that takes `IUsersApi`
- [ ] `LookupSlackUserTool` delegates to the shared service instead of owning
      the logic directly — existing behavior unchanged
- [ ] Init wizard creates an `IUsersApi` instance from the validated bot token
      (via SlackNet client factory) and passes it to the shared service
- [ ] Graceful failure: return empty list on API errors or missing scope
- [ ] Cache result for wizard session lifetime (same 5-min TTL pattern)

**Acceptance:** Both tool and wizard use the same user listing code. No
duplication. Existing tool behavior unchanged.

### 7. Rework ACL step with user search

- [ ] On step entry: call `ListUsersAsync` (cached after first call)
- [ ] If users available: show TextInputNode for search + filtered result list
- [ ] Filter by display name and real name (case-insensitive contains)
- [ ] Enter on result: set owner identity to selected user's ID
- [ ] If no users (missing scope): fall back to manual TextInputNode with
      explanation message
- [ ] Allowed users: similar search pattern, multi-select (a to add from
      search, d to remove, list shows selected users)

**Acceptance:** Owner selected by name search. Allowed users managed by
search. Falls back to manual ID entry when scope missing.

### 8. Update config generation

- [ ] `WriteConfig()` uses `SelectedPosture` directly (not `ResolveDeploymentPosture()`)
- [ ] Shell mode derived from posture in ViewModel
- [ ] `ChannelAudiences` written from `ChannelEntries` list
- [ ] Remove `ExposureMode` property and related config writes
- [ ] Remove webhook URL from Exposure (move to Identity sub-step or
      HealthCheck if still needed)
- [ ] Verify: generated config passes `netclaw doctor` schema validation

**Acceptance:** Config generated from explicit user choices, not inferred.
Doctor passes.

### 9. Move webhook URL collection

- [ ] Add webhook URL as optional sub-step in Identity step (after timezone)
      or as a prompt in HealthCheck
- [ ] Keep existing `_webhookUrlInput` TextInputNode
- [ ] Preserve "press Enter to skip" behavior

**Acceptance:** Webhook URL still collected, just in a different step.

### 10. Tests

- [ ] Unit test: `DeriveSecurityDefaults()` maps posture to shell mode + defaults
- [ ] Unit test: audience cycling wraps correctly (Team→Personal→Public→Team)
- [ ] Unit test: `ListUsersAsync` excludes bots and deactivated users
- [ ] Unit test: `ListUsersAsync` returns empty on missing scope
- [ ] Unit test: channel-add deduplication
- [ ] Unit test: DMs row cannot be removed
- [ ] Integration test: full wizard flow forward/back with new step order
- [ ] Manual test: run `netclaw init` end-to-end, verify config output
- [ ] `dotnet slopwatch analyze` — no new violations
