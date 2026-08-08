## 1. Baseline (precondition)

- [x] 1.1 Merge PR #1783 (connector-wide `MentionRequiredInThread`) to `dev`.
- [x] 1.2 Rebase `feature/mention-required-in-thread-per-channel` onto the updated `dev`; confirm the connector bool is present as the #1783 baseline the change deletes.
- [x] 1.3 Confirm the OpenSpec artifacts still pass `openspec validate --strict` after the rebase.

## 2. Per-channel config storage

- [x] 2.1 Add a per-channel `MentionRequiredInThread` value to `SlackChannelOptions`, `DiscordChannelOptions`, and `MattermostChannelOptions`, using the existing per-channel-map pattern (mirror `ChannelAudiences`). Keep `AllowedChannelIds` and `ChannelAudiences` additive — no shape migration.
- [x] 2.2 Update `netclaw-config.v1.schema.json`: add the per-channel value to all three channel sections; remove the connector-wide `MentionRequiredInThread`. Respect `additionalProperties: false` (Configuration Schema Sync Rule).
- [x] 2.3 Delete the connector-wide `MentionRequiredInThread`: the property on the three options classes and its schema entry. No migration — it was never deployed. Its #1783 routing reads move to the per-channel resolution in §3.1; land the deletion with §3.1 so the build stays green.
- [x] 2.4 Binding test: a config with per-channel `MentionRequiredInThread` values binds to the options; a channel with no entry resolves to `false`.

## 3. Routing gate (per-channel resolution)

- [x] 3.1 In each conversation actor (`SlackConversationActor`, `DiscordConversationActor`, `MattermostConversationActor`), resolve the effective per-channel `MentionRequiredInThread` for the message channel (default `false` when unset) and pass the resolved bool into `RoutingPolicy.Evaluate`. Keep the routing policy a pure function.
- [x] 3.2 Verify the routing behavior: an un-mentioned message in an active thread is held (not turned) when on; a mention creates the turn; a channel with no value keeps the active-session bypass; the value never grants access.
- [x] 3.3 Extend the cross-channel routing contract tests to resolve the per-channel value (on and off) across all three fixtures.

## 4. Backfill re-trigger on mention

- [x] 4.1 In `SlackThreadBindingActor` and the Discord/Mattermost binding actors, re-run the existing hydration on a mention when the channel's tap held messages. Guard: only on a mention, only with a real gap (cursor strictly before thread head), only when no turn is in flight.
- [x] 4.2 Reuse the existing fetch, gap computation, prompt-injection gate, and merge path. Add no new fetch path and no new watermark.
- [x] 4.3 Slack integration test proves a live (hot) actor re-hydrates the held gap on the second mention (fetchCount 1→2, no restart) and that the existing once-per-runtime path is unchanged when the tap is off. NOTE: Discord/Mattermost use the byte-identical re-arm + guard; dedicated per-channel integration tests are a follow-up.

## 5. Config TUI

- [x] 5.1 Broaden the `EditAudience` leaf (`ChannelsConfigViewModel` / `ChannelsConfigPage`) into a per-channel detail page: show and edit `MentionRequiredInThread` next to the audience question; persist like an audience change. (Space toggles; Enter persists alongside audience.)
- [x] 5.2 Seed `MentionRequiredInThread` at channel-add time from the assigned audience: `true` for Team or Public, off for Personal or a DM.
- [x] 5.3 Add a native smoke tape (`config-mention-thread`, registered in the light suite) covering the `EditAudience` leaf toggle (Off→On) and apply/autosave.

## 6. Docs and skills

- [x] 6.1 Update the per-channel config docs (`docs/spec/configuration.md`, the three channel integration pages, `slack-acl-policy.md`, and the adding-a-channel runbook) to describe the per-channel value and the removal of the connector-wide bool.
- [x] 6.2 No change needed: the `netclaw-operations` skill covers operational tasks (scheduling, doctor, approvals, MCP), not channel config format — there is no section documenting per-channel channel settings, so shoehorning this in would be out of place. The config reference lives in `docs/`.
- [x] 6.3 Updated netclaw-website issue #115 with the final per-channel behavior and the backfill-on-mention note.

## 7. Quality gates and OpenSpec close-out

- [x] 7.1 `dotnet build Netclaw.slnx` is clean; the routing (68), backfill (9), and TUI (75 + 299) test selections are green.
- [x] 7.2 `dotnet slopwatch analyze` reports no new violations (0 issues).
- [x] 7.3 `./scripts/Add-FileHeaders.ps1 -Verify` confirms copyright headers on all `.cs` files.
- [x] 7.4 The new `config-mention-thread` tape passes: it toggles the `EditAudience` leaf rule Off→On, applies, and its paired assertion (`tests/smoke/assertions/config-mention-thread.sh`) confirms `Slack.MentionRequiredInThreadByChannel.C01 = true` persisted. Full `./scripts/smoke/run-smoke.sh light` also run to confirm no regression in the shared Channels-editor tapes.
- [x] 7.5 Eval suite is NOT triggered by this change: it runs on identity/skill/memory/tool/model/SessionConfig changes; this change touches channel routing + config only, and §6.2 made no skill change. Skipped with justification.
- [x] 7.6 `openspec validate --strict` passed (verify); `openspec archive` syncs the delta specs into `openspec/specs/` and moves the change to `openspec/changes/archive/`.
