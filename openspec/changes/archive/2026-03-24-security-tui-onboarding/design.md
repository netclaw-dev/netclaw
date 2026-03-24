## Context

The init wizard has 9 steps with security configuration buried at step 7
(Exposure) where it's silently inferred. Users never see or confirm their
security posture. Channel audience assignment is auto-generated with no
override capability. User ID entry requires copy-pasting raw Slack IDs.

The TUI framework is Termina — keyboard-driven, no mouse support. Available
components: `SelectionListNode<T>`, `TextInputNode`, `DynamicLayoutNode`,
`PanelNode`. Key routing: ↑/↓ navigate lists, Enter confirms/advances,
Esc goes back. ←/→ are currently unused in the wizard.

**Current step order:**
Provider → ChatServices → ACL → Search → BrowserAutomation → [Memory] →
Exposure → Identity → HealthCheck

**Existing Slack API integration:**
- `auth.test` during ChatServices (validates bot token)
- `conversations.list` during HealthCheck (resolves channel names)
- No `users.list` call exists

## Goals / Non-Goals

**Goals:**
- Make security posture an explicit, early user decision
- Let users assign audience per-channel with keyboard-driven cycling
- Let users search for Slack users by display name instead of raw IDs
- Populate channel lists from Slack API for dynamic add/remove

**Non-Goals:**
- Adding mouse/click support to Termina
- MCP server audience assignment (future)
- Per-tool grant customization in the wizard (edit JSON manually)
- Webhook/notification audience integration

## Decisions

### D1: Wizard step reorder

**New order:**
1. Provider (unchanged)
2. ChatServices (simplified — channels broken out)
3. SecurityPosture (NEW — deployment posture selection)
4. ACL (reworked — user search via Slack API)
5. Channels (NEW — per-channel audience with ←/→ cycling)
6. Search (unchanged)
7. BrowserAutomation (unchanged)
8. Identity (unchanged)
9. HealthCheck (unchanged)

**Why SecurityPosture at step 3:** It must come after ChatServices (need to
know if Slack is enabled) but before Channels (audience defaults come from
posture). ACL moves after posture because the owner concept is security-
related.

**Why remove Exposure step:** The exposure mode (Local/Tailscale/Cloudflare)
was only used to infer posture. Making posture explicit eliminates the need
for the indirection. Network exposure details can be added as an advanced
config option later.

### D2: ←/→ for audience cycling (not Tab, not dropdown)

**Decision:** On a focused channel row, ←/→ cycles the audience value:
Team → Personal → Public → Team.

**Why not Enter:** Enter means "advance to next step" throughout the wizard.
Using it to edit a value would break the consistent navigation model.

**Why not Tab:** Tab could work for focus-switching between fields, but there
are only 3 audience values — cycling is faster and more discoverable.

**Why not a dropdown/popup:** Termina doesn't have dropdown components and
adding one is out of scope. Cycling with ←/→ is idiomatic for keyboard TUIs.

### D3: Channel list as custom DynamicLayoutNode (not SelectionListNode)

**Decision:** The channel list is a custom `DynamicLayoutNode` with per-row
rendering, not a standard `SelectionListNode`. Each row shows channel name,
ID, and audience value. A cursor index tracks the focused row.

**Why not SelectionListNode:** `SelectionListNode` fires `SelectionConfirmed`
on Enter, which would conflict with "advance to next step." We need Enter to
advance and ←/→ to edit. Custom rendering gives us full control over key
handling per row.

### D4: Slack users.list for owner search

**Decision:** Add `ListUsersAsync` to `ISlackProbe` that calls Slack
`users.list` with pagination. Cache the result for the wizard session. Show
a type-to-filter `TextInputNode` + filtered result list.

**Why not users.lookupByEmail:** Requires knowing the email. Display name
search is more natural — the user knows their Slack name, not necessarily
the email associated with their workspace.

**Scope:** The `users.list` call requires the `users:read` scope on the bot
token. If the scope is missing, fall back to manual ID entry with a helpful
message explaining why search isn't available.

### D5: conversations.list moved to Channels step

**Decision:** Move the `conversations.list` call from HealthCheck to the
Channels step. When the user presses `a` to add a channel, show a
type-to-filter list of workspace channels.

**Why:** Currently channels are entered as comma-separated names in a text
input and resolved later during health check. Moving resolution to the
Channels step provides immediate feedback and eliminates the "unresolved
channel" error in health check.

### D6: Audience defaults from posture

When SecurityPosture is selected, pre-populate channel audiences:

| Posture | DMs default | Channels default |
|---------|-------------|-----------------|
| Personal | Personal | Team |
| Team | Team | Team |
| Public | Public | Public |

User can override any channel's audience in the Channels step.

Shell mode derivation:
- Personal → HostAllowed
- Team → Off
- Public → Off

## Risks / Trade-offs

**[R1] users.list requires users:read scope**
→ Mitigation: Fall back to manual ID entry if scope is missing. Display
message explaining which scope to add.

**[R2] Large workspaces may have slow users.list pagination**
→ Mitigation: Cache the full user list on first call. Use client-side
filtering in the type-to-filter UI. Set reasonable timeout (15s).

**[R3] Reordering WizardStep enum may break back-navigation**
→ Mitigation: Back-navigation is step-based, not ordinal-based. Test all
forward/back transitions.

**[R4] Removing Exposure step loses webhook URL collection**
→ Mitigation: Move webhook URL to the HealthCheck step or add it as an
optional sub-step in Identity. Webhook configuration is not security-related.
