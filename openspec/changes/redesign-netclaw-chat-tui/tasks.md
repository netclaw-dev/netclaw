## 1. Planning and Issue Traceability

- [x] 1.1 Update `PRD-004` with the inline chat, structured event, input, copy, and approval requirements.
- [x] 1.2 Update `PRD-009` with the complete typed output and structured resume contract.
- [x] 1.3 Replace the old chat section in `TUI-001` with the approved named-region mockups and responsive rules.
- [x] 1.4 Update `SPEC-004` with the chat command, session picker boundary, and explicit presentation modes.
- [x] 1.5 Update `SPEC-002` and `SPEC-011` with output correlation, transport mapping, and structured resume behavior.
- [x] 1.6 Update `SPEC-010` with the headless, compatibility, and native terminal proof matrix.
- [x] 1.7 Update the current Netclaw GitHub issues `#577` and `#1338` with the approved scope and OpenSpec link.
- [x] 1.8 File the remaining Netclaw epic and child issues without duplicates, then add their links to this change.
- [x] 1.9 Update the current Termina GitHub issues `#45` and `#240` with the applicable scope and design link.
- [x] 1.10 File the remaining Termina epic and prototype issues without duplicates, then add their links to this change.

## 2. Termina Extend-Only Compatibility Foundation

- [x] 2.1 Add a public API approval baseline for the current Termina release.
- [x] 2.2 Add `TerminalPresentationMode` with stable explicit numeric values.
- [x] 2.3 Add `TerminaRuntimeOptions.PresentationMode` with `FullScreen` as the default.
- [x] 2.4 Append `NativeTerminal` to `ScrollInputMode` without a change to current values.
- [x] 2.5 Add `IInlineTerminalControl` without a change to `IAnsiTerminal`.
- [x] 2.6 Implement `IInlineTerminalControl` in `AnsiTerminal` and `VirtualTerminal`.
- [x] 2.7 Make `TerminaApplication` enter the alternate buffer only in `FullScreen` mode.
- [x] 2.8 Preserve direct `AnsiTerminal(bool)` behavior and make application dependency injection own buffer selection.
- [x] 2.9 Add full-screen regression tests for startup, render, resize, and exit behavior.
- [x] 2.10 Verify the approved API diff contains additive public changes only.

## 3. Termina Inline Coordinator Prototype

- [x] 3.1 Add an inline coordinator that owns a bounded live region in the primary buffer.
- [x] 3.2 Add the ordered erase, stable commit, and live redraw sequence.
- [x] 3.3 Add an additive `IInlineOutput` service for stable layout commits.
- [ ] 3.4 Route internal diagnostics through the inline output owner.
- [x] 3.5 Add `VirtualTerminal` tests for one stable commit and one live redraw.
- [x] 3.6 Add `VirtualTerminal` tests for parallel commits and deterministic output order.
- [x] 3.7 Add resize tests for narrower, wider, and wide-character live content.
- [x] 3.8 Add failure tests that verify cursor and terminal-mode recovery.
- [x] 3.9 Run the prototype on Linux terminals and tmux, then record the exact evidence.
- [ ] 3.10 Run the prototype on macOS and Windows Terminal, then record the exact evidence.
- [ ] 3.11 Accept inline mode only if resize, scrollback, selection, paste, and exit recovery pass the matrix.

## 4. Termina Input, Scroll, and Copy Primitives

- [x] 4.1 Add a text-history cancellation API that restores the saved draft.
- [x] 4.2 Add typed-key tests for Up, Down, draft restoration, and history cancellation.
- [x] 4.3 Verify `Shift+Enter` across legacy, Kitty, and native raw input paths.
- [x] 4.4 Add a visible capability result when a terminal cannot distinguish `Shift+Enter`.
- [x] 4.5 Add dimension-free scroll operations that use the measured viewport.
- [x] 4.6 Preserve mouse coordinates on wheel input and test route selection.
- [x] 4.7 Add semantic copy data that remains separate from display glyphs.
- [x] 4.8 Add clipboard failure output that preserves the selected semantic data.
- [x] 4.9 Add headless tests that exclude borders, control bytes, and truncated display text from copied data.

## 5. Netclaw Session Output Contract

- [x] 5.1 Add `ToolActivityOutput` with `CallId`, turn identity, safe phase, and safe summary.
- [x] 5.2 Relay current nonterminal tool activity through the session actor output boundary.
- [x] 5.3 Add additive `RunId` and parent `CallId` fields to `SubAgentOutput`.
- [x] 5.4 Populate stable sub-agent identities for start, activity, and completion events.
- [x] 5.5 Add nullable wire fields and a discriminator for every new output value.
- [x] 5.6 Map every current compaction, error, usage, file, turn, tool, and sub-agent field in both directions.
- [x] 5.7 Apply `OutputFilter.ToolCalls` to tool activity and sub-agent activity.
- [x] 5.8 Prove that Slack and other restricted subscribers do not receive the new activity.
- [x] 5.9 Prove that transient activity does not enter model context or the actor journal.
- [x] 5.10 Add DTO round-trip and old-payload fixtures for all additive fields.
- [ ] 5.11 Reject a missing, blank, or non-string rationale at the shared execution preflight.
- [ ] 5.12 Prove rejection, sibling isolation, no approval request, and legacy transcript compatibility.

## 6. Structured Session Resume

- [x] 6.1 Add a framework-owned settled transcript entry union with stable discriminators.
- [x] 6.2 Add nullable `RecentTranscript` properties without a change to `RecentMessages`.
- [x] 6.3 Add a bounded settled timeline to session state and snapshots with new serialization tags.
- [x] 6.4 Add settled transcript entries to `TurnRecorded` with new serialization tags.
- [x] 6.5 Build settled entries from user, assistant, tool, sub-agent, file, error, usage, and compaction events.
- [x] 6.6 Add read support for old journals and snapshots before new timeline writes start.
- [x] 6.7 Convert supported `SerializableChatMessage` history to explicit legacy transcript entries.
- [x] 6.8 Emit a diagnostic entry for unsupported legacy detail without a false active state.
- [x] 6.9 Emit both `RecentMessages` and `RecentTranscript` during the compatibility period.
- [x] 6.10 Add journal, snapshot, SignalR, and client resume fixtures across old and new shapes.

## 7. Netclaw Presentation Reducer and Visual Grammar

- [x] 7.1 Add immutable chat presentation state with keys for turns, tool calls, sub-agents, thoughts, and approvals.
- [x] 7.2 Add a pure reducer that maps every `SessionOutput` to state and explicit effects.
- [x] 7.3 Add parallel tool tests where results finish in a different order than calls.
- [x] 7.4 Add parallel same-name sub-agent tests that prove stable row identity.
- [x] 7.5 Add the `Session Header`, `Transcript`, `Activity Rail`, `Decision Gate`, `Composer`, and `Status Line` regions.
- [x] 7.6 Add borderless settled user, assistant, tool, thought, sub-agent, file, error, usage, and compaction forms.
- [x] 7.7 Add concise live forms and immutable settled forms for each event lifecycle.
- [x] 7.8 Add responsive layout rules and snapshots at 40, 60, 80, and 120 columns.
- [ ] 7.9 Add tail-follow state, a new-event count, and an explicit return-to-tail action.
- [x] 7.10 Replace fixed scroll dimensions with the actual measured viewport.
- [x] 7.11 Route all chat output through the inline output owner.
- [x] 7.12 Add a visible diagnostic for an unsupported output type or invalid lifecycle transition.

## 8. Composer, Approval, Inspector, and Copy Behavior

- [x] 8.1 Configure `Shift+Enter` for a newline and bare `Enter` for submission.
- [x] 8.2 Restore the saved draft after prompt history reaches its newest entry.
- [x] 8.3 Add double Escape with an injected `TimeProvider` and a defined interval.
- [x] 8.4 Give a pending approval priority over composer Escape behavior.
- [x] 8.5 Block paste delivery to a hidden composer while an approval owns focus.
- [x] 8.6 Preserve compact and expanded approval forms with `Ctrl+O`.
- [x] 8.7 Preserve the approval selection and bounded detail position across `Ctrl+O` changes.
- [x] 8.8 Render approval control characters as visible safe text.
- [x] 8.9 Add an inspector that shows complete event detail without transcript truncation.
- [x] 8.10 Queue inline output while the inspector owns the terminal and commit it after exit.
- [x] 8.11 Add semantic copy for an event and a complete turn.
- [x] 8.12 Add visible copy errors and keep the selected data after a failure.
- [x] 8.13 Keep the Composer visible and show every active-turn prompt in the Queue Shelf.
- [x] 8.14 Send active-turn prompts through the current session buffer and promote the full FIFO set together.
- [x] 8.15 Prove that three active-turn prompts produce one ordered follow-up model call.

## 9. Netclaw Command Integration

- [x] 9.1 Configure `netclaw chat` for explicit `Inline` and `NativeTerminal` modes.
- [x] 9.2 Keep init, config, provider, model, and session picker applications in `FullScreen` mode.
- [x] 9.3 Exit the session picker before a selected inline chat application starts.
- [ ] 9.4 Show a visible error when the selected chat application cannot start.
- [x] 9.5 Restore cursor, input, mouse, paste, and terminal modes on normal, canceled, and failed exits.
- [x] 9.6 Add command tests that prove each application selects its required presentation mode.

## 10. Package and Cross-Repository Integration

- [x] 10.1 Run all Termina unit, compatibility, and native prototype gates.
- [x] 10.2 Select a dotted SemVer prerelease that follows the Termina release process.
- [x] 10.3 Publish the Termina prerelease package and record its package and commit links.
- [x] 10.4 Update Netclaw to the prerelease with the repository package workflow.
- [x] 10.5 Restore and build Netclaw against the published package, not a local binary.
- [x] 10.6 Record explicit rollback steps for the package and the Netclaw presentation choice.

## 11. Verification and Completion

- [x] 11.1 Add headless tests that inject every `SessionOutput` subtype.
- [x] 11.2 Add typed-key tests for prompt, paste, history, Escape, approval, inspector, and copy flows.
- [x] 11.3 Record and review the three disposable visual checkpoint videos outside the repository.
- [x] 11.4 Run `./scripts/smoke/run-smoke.sh light` and retain the result.
- [x] 11.5 Run the focused Netclaw actor, protocol, CLI, and TUI test suites.
- [x] 11.6 Run `dotnet slopwatch analyze` in each repository that contains code changes.
- [x] 11.7 Run `./scripts/Add-FileHeaders.ps1 -Verify` for Netclaw C# changes.
- [ ] 11.8 Verify each issue acceptance criterion against tests or native evidence.
- [ ] 11.9 Run OpenSpec verification and resolve every mismatch.
- [ ] 11.10 Sync the approved delta specifications and archive the completed change.
