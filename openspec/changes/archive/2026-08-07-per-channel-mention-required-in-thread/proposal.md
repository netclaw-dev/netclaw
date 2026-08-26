## Why

Today, once a thread has an active session, Netclaw replies to every message in that
thread without a mention. In a busy shared channel this is noisy — Netclaw answers side
talk between people that was never meant for it. Operators have no per-channel opt-out.

This change adds a per-channel tap on how a channel pushes messages to the LLM. When the
tap is on for a channel, un-mentioned thread messages stop flowing to the bot after the
first mention. A later mention re-runs the existing thread-history backfill to catch up.
The change touches only what makes the LLM respond. It adds no new security mechanism.

Source: issue #1782 (feature request) and PR #1783 (a first, connector-wide
implementation). There is no dedicated PRD; the design record is memorizer memory
`167dc069` (v13).

## What Changes

- Add a per-channel `MentionRequiredInThread` value for Slack, Discord, and Mattermost.
  When it is on for a channel, Netclaw ignores an un-mentioned message in an active thread.
- On a mention, Netclaw re-runs the existing thread-history backfill and processes the
  messages that accumulated while the tap held them back. This reuses the current
  hydration path. It adds no new mechanism.
- Remove the connector-wide `MentionRequiredInThread` bool that PR #1783 adds. It was never
  deployed, so no migration — just delete it. There is no connector-wide value and no
  workspace-wide default.
- Seed the per-channel value at channel-add time. For a public or team channel, write
  `true`. For a personal channel or a DM, leave it off. This is a write-time default from
  the audience the operator gives.
- Add the `MentionRequiredInThread` toggle to the per-channel `EditAudience` leaf in
  `netclaw config`. The leaf becomes a small per-channel detail page. The operator edits
  the value after add time.
- File shares follow the same rule as a text message (candidate — verify current behavior
  against #1782 in the specs phase).

## Capabilities

### New Capabilities

- none. This change extends existing capabilities.
- Note: the mention-gate routing rule is not specified in OpenSpec today. This change adds
  those requirements to `netclaw-input-adapters` and the three socket specs. If the specs
  phase finds no suitable home, it may introduce a `channel-mention-gating` capability then.

### Modified Capabilities

- `netclaw-input-adapters`: add the channel-agnostic per-channel `MentionRequiredInThread`
  rule and its value resolution. A channel carries its own value; a channel with no value
  defaults to `false` (today's behavior).
- `netclaw-slack-socket`, `netclaw-discord-socket`, `netclaw-mattermost-socket`: add the
  per-channel mention gate and the per-channel config value; delete the connector-wide bool
  from PR #1783 (never deployed, no migration); re-run hydration on a mention.
- `thread-history-backfill`: relax the "hydrate at most once per runtime" rule so a mention
  re-hydrates the gap that the tap accumulated. The existing prompt-injection gate does not
  change.
- `channel-audience-tui`: the per-channel `EditAudience` leaf gains the
  `MentionRequiredInThread` toggle. The add-channel step seeds the value from the channel
  audience.

## Impact

Affected code:

- Routing policies: `SlackRoutingPolicy`, `DiscordRoutingPolicy`, `MattermostRoutingPolicy`
  — resolve the per-channel value before the routing decision.
- Backfill trigger: `SlackThreadBindingActor` and the Discord/Mattermost binding actors —
  re-run the existing hydration on a mention when the tap held messages. Guard the
  duplicate-content case: do not fetch on every inbound; fetch only on a mention with a
  real gap and no in-flight turn.
- Config: `SlackChannelOptions` / `DiscordChannelOptions` / `MattermostChannelOptions` —
  the per-channel value; `netclaw-config.v1.schema.json`; delete the connector bool.
- Config TUI: `ChannelsConfigViewModel` / `ChannelsConfigPage` — the `EditAudience` leaf and
  the add-channel seed.
- Docs and skills: the per-channel config docs; the `netclaw-operations` skill;
  netclaw-website issue #115.
- Tests: routing contract tests; a backfill-re-trigger test per channel; config binding;
  a smoke tape for the leaf.

Dependencies and order:

- This change follows PR #1783 (merged). It deletes the connector-wide bool #1783 added.
  This branch is rebased onto the updated `dev`.

In scope (MVP):

- The per-channel value, the mention tap, the backfill re-trigger on a mention, the
  add-time seed, the `EditAudience` toggle, and the deletion of the connector-wide bool.
- All three channels: Slack, Discord, Mattermost.

Out of scope:

- Any connector-wide or workspace-wide default.
- Any change to ACL, audience resolution, or the prompt-injection gate. This change reuses
  them as-is.
- A config-shape migration for `AllowedChannelIds` or `ChannelAudiences`. The storage stays
  additive.

Security and operational impact:

- No new security mechanism. Backfilled history flows through the existing hydration path,
  which already applies the live multi-party rules — sender trust, audience, and the
  prompt-injection gate.
- The tap is default off. A channel with no value keeps today's behavior.
- No config migration. The connector-wide bool was never deployed, so nothing needs
  migrating; the change deletes it.
