## Context

Netclaw chat uses one mutable text node inside a full-screen Termina application.
This model hides typed events and makes parallel activity difficult to correlate.
The alternate terminal buffer also removes native scrollback, search, and selection.

Netclaw and Termina are public .NET libraries with released API contracts.
This change must extend those contracts without changes to current signatures or defaults.
Termina full-screen mode must remain the default for all current applications.

The session actor publishes a typed `SessionOutput` union through filtered subscriptions.
SignalR converts that union to the flat `SessionOutputDto` wire contract.
The TUI currently converts these events to text before it presents them.

The session journal already stores tool batches, tool results, approvals, and completed turns.
`SerializableChatMessage` retains tool call IDs, tool names, arguments, results, and message roles.
The current resume DTO only exposes role and text content through `RecentMessages`.

This design affects the Netclaw CLI, the daemon wire contract, session persistence, and Termina.
It also affects users who depend on terminal scrollback, keyboard input, approvals, and copy behavior.

## Goals / Non-Goals

**Goals:**

- Give chat a clear visual grammar with named semantic regions.
- Use the primary terminal buffer for an explicit Netclaw chat mode.
- Preserve complete structured output and stable correlation identities.
- Keep settled transcript content immutable and easy to select.
- Preserve approval context and the current `Ctrl+O` detail control.
- Use `Shift+Enter` for a newline and `Enter` for prompt submission.
- Keep the Composer available during active work and show all queued prompts.
- Preserve the session actor's FIFO batch for active-turn prompts.
- Add safe prompt history cancellation and semantic copy.
- Restore structured settled history after a session resume.
- Preserve released API, wire, persistence, and full-screen behavior.
- Prove behavior with deterministic tests and disposable visual checkpoints.

**Non-Goals:**

- Replace Termina full-screen mode.
- Change the default mode for a current Termina application.
- Provide terminal-native search or scrollback inside full-screen mode.
- Persist transient tool progress or raw thought text in model context.
- Make terminal-native selection omit arbitrary characters in a selected row.
- Change the daemon execution engine or its actor ownership model.

## Decisions

### D1: Preserve all released contracts through additive changes

The implementation will follow an extend-only rule for public and persisted contracts.

- It will not remove or rename a public type or member.
- It will not change a public member signature.
- It will not add an optional parameter to a current method.
- It will not add a required member to a current public interface.
- It will preserve current enum names, values, and numeric assignments.
- It will append new enum values with explicit numeric assignments.
- It will add new DTO fields as nullable properties.
- It will reserve new Protobuf tags and preserve all current tags.
- It will keep old read paths before it enables new write paths.
- It will keep `RecentMessages` until a separate removal policy permits removal.

Termina will add new types and services for new behavior.
Netclaw will add new output records and DTO fields for new event data.
Compatibility tests will approve the public API and serialized fixtures.

An alternative would change current interfaces and constructors.
That choice would reduce new type count, but it would break external implementations and compiled applications.

### D2: Select one presentation mode for each application instance

Termina will add `TerminalPresentationMode` with these values:

- `FullScreen = 0`
- `Inline = 1`

`TerminaRuntimeOptions.PresentationMode` will default to `FullScreen`.
The property will not use `required`.
The current `AnsiTerminal(bool)` constructor will remain unchanged.

Termina will append `NativeTerminal = 2` to `ScrollInputMode`.
Netclaw chat will select `Inline` and `NativeTerminal` explicitly.
The init wizard and the session picker will retain `FullScreen`.

The session picker will exit its Termina application before it starts chat.
Chat will start as a new inline application after a successful selection.
This boundary prevents one terminal host from changing modes during one application lifetime.

Per-route presentation modes were considered.
They would make terminal ownership and exit recovery dependent on navigation state.

### D3: Keep the full-screen render path and add an inline coordinator

The current `DiffingTerminal` path will remain the full-screen implementation.
The inline path will use a new internal coordinator on the primary buffer.
The coordinator will own one bounded live region below the settled transcript.

Termina will add an interface such as `IInlineTerminalControl`.
The interface will contain the relative cursor and erase operations that inline mode needs.
`AnsiTerminal` and `VirtualTerminal` will implement the new interface.
The change will not add members to `IAnsiTerminal`.

Termina dependency injection will construct `AnsiTerminal(false)` for application ownership.
`TerminaApplication` will enter the alternate buffer only for `FullScreen`.
Direct users of `AnsiTerminal(bool)` will retain the current constructor behavior.

The inline coordinator will track the live region row count and terminal width.
It will erase only rows that it owns.
It will calculate the next live layout before it changes terminal output.

The coordinator will use this commit sequence:

1. Erase the tracked live region.
2. Write the new stable block to the primary buffer.
3. End the stable block with a normal line break.
4. Draw the current live region again.
5. Record the new live row count and width.

The prototype must prove resize behavior before this design ships.
An unsupported terminal or an invalid mode will cause a visible startup error.
The runtime will not fall back to full-screen mode without an explicit user choice.

A complete primary-buffer diff engine was considered.
That design would recreate terminal scrollback and would risk changes to stable rows.

### D4: Give one service ownership of inline output

Termina will add an additive service such as `IInlineOutput`.
The service will commit stable `ILayoutNode` blocks through the inline coordinator.
Each asynchronous method will require a `CancellationToken` parameter.

Netclaw will route all chat output through this service or the live root update path.
Background diagnostics will enter the same ordered output queue.
Direct `Console.Out` writes during inline chat will be a contract violation.

The queue will preserve event order from each producer.
Stable correlation IDs will resolve order differences between parallel producers.
An output failure will stop chat and show a visible terminal recovery error.

A global console redirection was considered.
It cannot retain semantic event data and can hide output ownership defects.

### D5: Use a pure Netclaw presentation reducer

`ChatPage` will not append formatted text directly to one mutable transcript node.
A pure reducer will map each `SessionOutput` to immutable presentation state and effects.

The state will use these stable keys:

- `ToolCallId` for each tool activity.
- `RunId` for each sub-agent run.
- The parent `ToolCallId` for each sub-agent group.
- A turn identity for assistant text, thought state, and usage.
- The request `ToolCallId` for each approval.

The reducer will produce these results:

- A stable block that the coordinator must commit.
- A live-region snapshot.
- An input or approval mode transition.
- A diagnostic for an unsupported or invalid event.

The event lifecycle will remain separate from its display state.
For example, a tool can run while its detail stays collapsed.
An expand action will not change the tool phase.

The named regions will be `Session Header`, `Transcript`, `Activity Rail`, `Decision Gate`, `Composer`, and `Status Line`.
The reducer will apply the responsive rules at 40, 60, 80, and 120 columns.

A set of event-specific UI mutations was considered.
That model would repeat state rules and would make event-order tests difficult.

### D6: Extend session output with correlated activity records

Netclaw will add `ToolActivityOutput` as a new `SessionOutput` subtype.
It will include `CallId`, the turn identity, a safe phase, and a safe summary.
The session pipeline will publish current tool progress instead of discarding it.

`SubAgentOutput` will gain nullable or defaulted additive properties for `RunId` and parent `CallId`.
New producers will populate both fields.
Old producers and old DTO payloads will remain readable.

`SessionOutputTypes` will add a new discriminator for tool activity.
`SessionOutputDto` will add nullable fields for each new value.
The mapper will define both directions for every supported output type.

The current `OutputFilter.ToolCalls` flag will cover the new activity record.
This choice preserves current filter bit values and subscriber policy.
The TUI will request the complete applicable filter set.
Slack and other channels will retain their current filters.

Tool progress and raw thought deltas will remain transient.
They will not enter model context or the actor journal.

A new filter flag was considered.
It would change subscriber configuration without a separate security or volume requirement.

### D7: Preserve structured resume data through an additive timeline

`SessionJoined.RecentMessages` and `SessionOutputDto.RecentMessages` will remain unchanged.
The contracts will add a nullable `RecentTranscript` collection.
Each item will use a new framework-owned domain DTO with a stable discriminator.

The timeline will contain settled user, assistant, tool, sub-agent, file, error, usage, and compaction entries.
It will not contain active states or transient progress.
It will not use TUI node types or style values.

The session state will keep a bounded settled timeline for the configured recent turn window.
`TurnRecorded` will gain an additive transcript collection with new Protobuf tags.
Current tool and approval journal events will retain their schemas.
The snapshot will gain the same domain timeline with new tags.

New code will read an absent timeline as a legacy record.
It will derive supported entries from `SerializableChatMessage` data.
Unsupported legacy detail will produce an explicit diagnostic entry.
It will not invent an active state.

During the migration, the daemon will emit both `RecentMessages` and `RecentTranscript`.
A new client will prefer `RecentTranscript` when it is present.
An old client will ignore the new JSON property and use `RecentMessages`.

The implementation will add read support and fixture tests before new writes start.
It will verify old journal data, old snapshots, and old SignalR payloads.

Replacing `RecentMessages` was considered.
That choice would break current clients and would remove the only legacy resume path.

### D8: Configure input through additive Termina behavior

Netclaw will configure the current text area with `WithNewlineModifier(ConsoleModifiers.Shift)`.
Bare `Enter` will submit the prompt.
The native input path must preserve `Shift+Enter` through Kitty keyboard input or raw input.

The Composer will remain visible while a model, tool, or sub-agent works.
Each later prompt will use the current `SendMessage` path immediately.
The session actor will remain the batch owner.
Its `Processing` handler will retain accepted prompts in FIFO order.
The actor will drain the full buffer before one follow-up model call.
The client will not send one queued prompt after each completed turn.

The Queue Shelf will show each prompt that waits behind the current turn.
A successful current-turn completion will promote the full displayed set.
A failed send will keep its prompt for the current reconnect path.
The reconnect path will retain FIFO order and will not discard a queue entry.

Termina will add a prompt-history cancellation API if the current component cannot restore drafts.
The new API will not change a current text-area method signature.
Netclaw will use it for Up and Down history navigation.

Netclaw will implement double Escape with an injected `TimeProvider`.
The first Escape will preserve the current text.
A second Escape inside the defined interval will clear recalled or current text.

A pending approval owns Escape before the composer does.
One Escape will deny the approval according to the current approval contract.
Paste input will route only to a composer that accepts paste.

If the terminal cannot distinguish `Shift+Enter`, chat will report the unavailable shortcut.
It will not select another shortcut without an explicit configuration.

### D9: Keep approval state inside the Decision Gate

The compact Decision Gate will show the target, effect, scope, and selected decision.
`Ctrl+O` will switch between compact and expanded detail.
The switch will preserve the selected decision and scroll position where possible.

The expanded form will use a bounded detail view.
Page Up and Page Down will move through long detail.
The renderer will show control characters as safe visible text.
It will never write approval content as terminal control bytes.

An approval response will use the current actor command and security checks.
The TUI will not create a separate approval policy.

### D10: Separate display text from semantic copy text

Settled transcript blocks will avoid decorative side borders and corner characters.
This choice improves native terminal selection for ordinary transcript content.

Termina will add an additive semantic copy contract for components that have hidden detail.
The contract will expose plain text without ANSI control bytes or border glyphs.
Netclaw will use it for complete tool results, approval detail, and diagnostics.

Terminal-native selection cannot make selected cell characters unselectable.
The design will not claim that border characters can become unhighlightable.
The borderless transcript and semantic copy path will reduce the practical defect.

A custom terminal selection system was considered.
It would duplicate emulator behavior and would not work consistently through tmux or remote shells.

### D11: Use an explicit inspector for complete event detail

The transcript will show concise settled rows.
The inspector will show complete semantic data for the selected event.
The first implementation may use a temporary full-screen Termina application.

The inspector will close before inline chat resumes output.
The inline coordinator will commit queued stable blocks after the inspector exits.
The inspector will use the same redaction and control-character policy as semantic copy.

This choice keeps the inline transcript quiet without data loss.
It also avoids a large bordered panel in the primary scrollback.

### D12: Prove compatibility and review the visual grammar

Termina tests will approve the public API surface for the released baseline and the new surface.
They will verify the numeric values of all changed enums.
They will verify that `FullScreen` remains the default.

`VirtualTerminal` tests will cover commits, parallel arrivals, resize, cursor recovery, and failures.
The full-screen test suite will prove no visible behavior change for current applications.

Netclaw headless tests will cover every `SessionOutput` disposition.
They will cover parallel tools that finish out of order.
They will cover structured resume and old payload conversion.

Typed-key tests will cover `Shift+Enter`, history draft restoration, double Escape, and approval priority.
They will use `TimeProvider` and will not use time delays.

Three disposable video checkpoints will cover the inline chat path.
They will cover the core chat, rich activity with approval, and the Inspector.
The last checkpoint will also cover narrow width and resize behavior.

Each tape will stay under `/tmp` and outside the repository.
Each review will use the video and selected lossless frame images.
The reviewer will record material visual defects before the next checkpoint.
The tapes will not become CI assets or permanent smoke tests.

### D13: Enforce rationale at the shared execution preflight

Every generated tool schema already marks `_rationale` as a required string.
Some providers can still omit it from a tool call.
The shared executor preflight will reject a missing, blank, or non-string value.
The rejection will become the tool result for that call.
The tool will not execute and no approval prompt will appear.
The next model step can issue a corrected call with a rationale.

The validation will apply to new execution only.
Persistence extraction and transcript reads will continue to accept a null
rationale from old records. The TUI will mark that old value as unavailable.
It will not infer intent from tool arguments.

Parallel tool calls will retain per-call failure isolation.
A call without rationale will fail before dispatch.
A compliant sibling call can still execute.

## Actor Boundaries and Persistence

The session actor will remain the owner of session lifecycle and durable state.
The tool pipeline will publish activity to the session actor through current actor messages.
Subscribers will receive activity through the current filtered pub/sub boundary.

The SignalR actor will remain a transport adapter.
It will map fields without UI policy or lifecycle inference.
The CLI reducer will own presentation state and responsive style.
Termina will own terminal buffers, cursor state, and output order.

The journal will store only settled framework-owned transcript data.
It will not store live UI state, expanded state, cursor state, or transient progress.
The actor will rebuild a bounded transcript from journal events and snapshots.

## Failure Modes and Recovery

- An inline startup failure will restore terminal modes and return a nonzero result.
- A coordinator write failure will stop new commits and restore the cursor when possible.
- A direct console write will produce a visible ownership diagnostic in development and tests.
- An unknown output discriminator will produce a diagnostic event without false lifecycle state.
- A missing correlation ID from an old payload will use a marked legacy row.
- An invalid approval payload will block the decision and show an error.
- A clipboard failure will retain the selected data and show a visible error.
- A client disconnect will retain durable actor state and discard only transient UI state.
- A prompt send failure will retain the prompt for ordered reconnect delivery.
- A process failure will rely on the current session recovery path and the settled timeline.
- A resize proof failure will block inline mode release for that terminal class.

## Risks / Trade-offs

- [Primary-buffer reflow can invalidate tracked rows] -> The prototype will test resize and wide-character cases before package release.
- [User scroll can conflict with new live output] -> The coordinator will limit changes to its bottom live region and test terminal behavior.
- [A third-party console writer can corrupt the live region] -> Netclaw will route output through one service and detect known direct writes.
- [Keyboard protocols differ across terminals] -> Native tests will define the supported matrix and visible failure behavior.
- [A structured timeline increases persisted data] -> The actor will bound it to the recent turn policy.
- [New detail can expose sensitive values] -> Existing output filters, redaction, and approval controls will remain in force.
- [Old clients ignore new activity] -> The daemon will keep current fields and discriminators while it adds new data.
- [An enum addition can expose incomplete switches] -> Tests and analyzers will find all Netclaw and Termina switches before release.
- [A temporary inspector pauses inline updates] -> The coordinator will queue events and commit them after inspector exit.
- [Borderless rows reduce grouping cues] -> Indentation, symbols, spacing, and color will carry the visual hierarchy.

## Migration Plan

1. Update the source PRD, engineering specification, and TUI mockups.
2. Add Termina API approval tests for the released baseline.
3. Add the new Termina enums, options, interfaces, and full-screen regression tests.
4. Build the inline coordinator against `VirtualTerminal`.
5. Run the native terminal prototype matrix.
6. File or update the approved Netclaw and Termina issues with prototype evidence.
7. Publish a dotted SemVer Termina prerelease after all Termina gates pass.
8. Update Netclaw through the package management workflow.
9. Add the Netclaw presentation reducer and named regions.
10. Add new session output records and complete SignalR mappings.
11. Add structured timeline read support and legacy fixtures.
12. Enable structured timeline writes after the read tests pass.
13. Add typed-key, headless, responsive, and disposable visual proof.
14. Run Slopwatch, file-header verification, and the required smoke suite.
15. Verify the OpenSpec change before archive.

### Rollback

Full-screen mode will remain the Termina default throughout the migration.
A Netclaw rollback can select the prior package and restore its explicit full-screen chat configuration.
A Termina package rollback can remove the prerelease reference without a data conversion.

The daemon will continue to emit `RecentMessages` during the migration.
New persisted fields will use additive tags, so older readers can ignore them.
The team will disable new writes before a rollback if an old reader cannot retain unknown fields.

The runtime will not perform a silent mode fallback.
An operator must select a different mode or package version explicitly.

## Open Questions

- Which cursor sequence set remains reliable after primary-buffer resize on each supported terminal?
- Should the inspector always use a temporary alternate buffer?
- Which settled thought summary can the product policy retain?
- Which terminal versions will define the supported native matrix?
- What exact byte and entry limits will bound `RecentTranscript`?
- Can the current journal serializer retain unknown fields across every supported rollback path?
