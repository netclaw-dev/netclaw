# Session contract tests and the first substantial extraction

**Recommendation:** strengthen the narrow tool-result baseline, then extract tool-result acceptance and batch continuation under the same tests.
This protects user-visible behavior while the actor transfers real state and decisions.
Keep a bounded Accordant pilot as the final support PR for this actor refactor.
Keep the existing actor tests as the integration proof.

Baseline: `4753b7682c353dc3b7e3932f458124052dbd6adb` on `dev`.
The status section separates completed proof from proposed work.
The user authorizes implementation and verification. The root reviews each result before PR publication.
Hold the whole open stack until every PR head has green required checks. Then request one native stack group merge.
Each PR must preserve accepted behavior. A behavior change needs a separate decision.

## Current checkpoint: 2026-10-14

The approved structural refactor is merged at [`90728b20691334694d8f57e0bf61b77b28d9901b`](https://github.com/netclaw-dev/netclaw/commit/90728b20691334694d8f57e0bf61b77b28d9901b).
PRs [#2449](https://github.com/netclaw-dev/netclaw/pull/2449), [#2451](https://github.com/netclaw-dev/netclaw/pull/2451), and [#2454](https://github.com/netclaw-dev/netclaw/pull/2454) merged as one native group.
PR [#2455](https://github.com/netclaw-dev/netclaw/pull/2455) then merged the adjacent child-result envelope change.
All four pull requests passed their required head checks.
Each expected post-merge-only check skipped.
The four post-merge runs on `90728b20691334694d8f57e0bf61b77b28d9901b` passed: Docker `38496919949`, verification `38496919940`, and CodeQL `38496919828` and `38496919826`.

The existing method counted committed source at baseline `4753b7682c353dc3b7e3932f458124052dbd6adb`.
It counted source at final commit `90728b20691334694d8f57e0bf61b77b28d9901b`.
The actor fell from 5,513 to 3,727 lines and from 87 to 43 instance fields.
Production C# grew from 202,618 to 202,619 physical lines across 945 to 948 files.
The user accepted this result and closed the structural phase.
Production source grew by one line, so the production-line reduction target remains unmet.
The final capability review found no further approved structural candidates.

The bounded Accordant/CsCheck pilot is merged in `ActiveToolBatchAccordantTests.cs`.
The merged tree contains nine canonical skills under `.agents/skills/accordant-*` and nine resolving Claude links under `.claude/skills/accordant-*`.
The [Codex skills guide](https://developers.openai.com/codex/skills/), [Claude Code skills guide](https://code.claude.com/docs/en/skills), and [OpenCode skills guide](https://docs.opencode.ai/docs/skills/) document these project discovery paths.
Filesystem path checks passed for all three clients.
These checks do not prove that an active client session refreshed its skill catalog.

Background-child wake remains a separate accepted capability and is not part of the closed structural phase.
Its feature work remains local and unmerged.
Its focused SQLite recovery proof passed two tests.
The proof does not establish child delivery or whole-process crash recovery.
That work does not show that the original synchronous actor was defective.
It does not block structural-phase closure.
The structural phase has no approved refactor work left.
Any new simplification needs new evidence and review.

## Scope and evidence

The first boundary covers accepted tool outcomes, completed-batch state, and the decision to continue the current turn.
It does not change authorization, tool execution, loop limits, cycle policy, journal schemas, or process durability.
This is the first part of a substantial actor refactor. A small retry helper extraction cannot satisfy the full objective.
Later work must transfer context, input/turn progression, and lifecycle responsibilities where evidence supports those boundaries.

Traceability: [PRD-001 FR-002, FR-003, FR-011](../prd/PRD-001-netclaw-mvp.md),
[session protocol](../spec/SPEC-002-session-lifecycle-and-protocol.md),
[tool execution encapsulation](../../openspec/specs/netclaw-session/spec.md#requirement-tool-execution-encapsulation),
and [session state rules](../../openspec/specs/session-state-machine/spec.md).
Use the [engineering glossary](../spec/GLOSSARY.md) for shared terms.
The model oracle means independent test rules that predict permitted observable results.

The actor repeats outcome work in `HandleToolExecutionCompleted` and `ProcessToolCallResult`.
It repeats continuation choices in `HandleToolExecutionCompleted` and `CompleteToolBatch`.
See [actor:876](https://github.com/netclaw-dev/netclaw/blob/4753b7682c353dc3b7e3932f458124052dbd6adb/src/Netclaw.Actors/Sessions/LlmSessionActor.cs#L876)
and [actor:4870](https://github.com/netclaw-dev/netclaw/blob/4753b7682c353dc3b7e3932f458124052dbd6adb/src/Netclaw.Actors/Sessions/LlmSessionActor.cs#L4870).
These paths combine result presentation, receipts, child outcomes, context changes, files, media, and continuation.
The extraction must remove duplicate decisions, not merely relocate methods.

[PR #2420](https://github.com/netclaw-dev/netclaw/pull/2420) overlaps this boundary.
Its changed files include `LlmSessionActor`, `ActiveToolBatchTracker`, `TurnStateTracker`, `ToolExecutionIntegrationTests`, and `ToolCycleUserInputTests`.
The root identified #2421 and #2422 as dependent work.
Do not adopt unpublished recurrence behavior. Recheck `dev` before each PR and reassess overlap after an upstream merge.
Keep the existing loop/cycle decisions unchanged in this extraction.

## Existing proof and the narrow gap

| Existing actor test | Observed boundary to retain |
| --- | --- |
| [ToolExecutionIntegrationTests:68,121,166](../../src/Netclaw.Actors.Tests/Sessions/ToolExecutionIntegrationTests.cs) | Single/multiple tool calls, failure result, model continuation, invocation counts |
| Same file:340,360,381 | Only the correct successful receipt changes project scope; presentation text cannot grant scope |
| [ToolBatchHistoryWedgeTests:80](../../src/Netclaw.Actors.Tests/Sessions/ToolBatchHistoryWedgeTests.cs) | Partial failure closes every call contiguously in the next provider request |
| [ToolCycleUserInputTests:54,75,107](../../src/Netclaw.Actors.Tests/Sessions/ToolCycleUserInputTests.cs) | New input and continuation retain distinct budget/reset behavior |
| [ApprovalRehydrationTests:606,801,1103,1146,1198](../../src/Netclaw.Actors.Tests/Sessions/ApprovalRehydrationTests.cs) | No completed-sibling replay; uncertain-effect restraint; expired and wrong-requester rejection; original audience |
| [ApprovalTurnBoundaryTests:86,367](../../src/Netclaw.Actors.Tests/Sessions/ApprovalTurnBoundaryTests.cs) | Mixed approval decisions execute only permitted calls |

This inventory came from source review. The status section records subsequent test execution.
The first test author must inspect exact assertions before adding any equivalent case.
Add a small completion-order theory only where existing checks miss result identity, multiplicity, or premature continuation.
Use two independent calls with distinct arguments and results. Release each fake tool through an explicit task signal.
Assert one continuation, all expected provider results exactly once, and no duplicate invocation in either completion order.
Strengthen the partial-failure assertion only if it omits result multiplicity or a successful follow-up.

The current fake model already accepts real `FunctionCallContent` values.
For example, `ToolExecutionIntegrationTests:123` supplies two `web_search` calls with different `query` arguments.
`ApprovalRehydrationTests:613` supplies `read_file(path)` and `shell_execute(command)` calls.
The adapter must preserve each call ID, name, argument, and result association.
Fake tools prove actor dispatch, receipt interpretation, observable outputs, and execution counts.
They do not prove real shell effects, dispatcher security, provider intelligence, or transport delivery.
Retain the separate real-dispatcher and pipeline tests for those boundaries.

## A small actor contract

The contract can exercise the entire current actor before extraction.
Use public session commands, subscriber events, and controlled model/tool responses through the existing test base.
Represent only submitted calls, observed results, pending decisions, terminal outcome, and continuation count.
Do not reproduce actor phases or call production policy code from the oracle.

| Operation | Input domain and precondition | Required observations and postcondition |
| --- | --- | --- |
| Submit turn | Fresh input; one or two calls; distinct IDs and argument values. No active turn for this first model. | Admission reply; exact fake invocations; tool-call outputs. Admission does not mean completion. |
| Complete fake call | An issued unresolved call; success or tool failure; distinct result text. | Corresponding result identity; retained call/result association. No continuation while another required result remains unresolved. |
| Answer tool prompt | Known, expired, or unknown call ID; original or wrong requester; allow or deny. Requires a session, not a valid answer. | Accepted answer follows existing authority; invalid answer cannot execute the tool or authorize another call. Preserve existing error output. |
| Complete model response | A requested model response; text or a further bounded tool batch. | Provider input contains each required result exactly once. Final output and `TurnCompleted` match the selected outcome. |
| Respawn actor | An established durable approval wait; exclude active uncertain-effect exploration from the first generated model. | Rejoin succeeds; accepted completed siblings do not execute again; original authority remains. Existing uncertain-effect tests remain required. |

Global rules: no premature success, no duplicate tool effect, no authority from presentation text, and no lost call/result pairing.
Existing tests cover rejected or stale prompt responses. Do not exclude those inputs through generation preconditions.
A stale internal completion is a separate future contract unless the extraction changes its correlation boundary.
Do not create an external operation that injects arbitrary private actor messages merely to enlarge the model.

Positive example: B finishes before A; one continuation receives both correctly associated results.
Negative example: an old prompt ID or another requester cannot release a pending authorized call.
These examples validate the oracle. They are not the complete generated suite.

For the current component pilot, CsCheck chooses bounded sequences, call IDs, payloads, repetitions, and completion probes.
Accordant checks each observed result and readiness response through `Spec.Allows`.
The pilot uses two issued call IDs per batch and no invented unknown-result contract.
Inspect executed case and transition counts. Keep the seed and exact operation trace for replay.
The later actor contract retains the invalid approval-answer inputs in the table above.
An operation trace needs task gates and acknowledgements to reproduce actor timing.

Use Arrange/Act/Assert with xUnit. Share assertions through theories when the tested behavior is the same.
The test adapter may change internally after extraction; accepted oracle rules and external assertions must remain unchanged.

## Decisions and proof limits

| Intended behavior and benefit | Evidence and prior-design question | Decision and proof |
| --- | --- | --- |
| Safe continuation prevents incomplete provider history and duplicate work. | Existing tests prove selected paths; source repeats outcome/continuation work. Does the split still serve a necessary delivery distinction? | Keep ingress-specific commit order. Consolidate domain rules. Prove both completion orders and partial failure. |
| Durable approval resumption lets the UI safely resume interrupted work. | The test base uses in-memory journal/snapshots. Does this prove process durability? No. | Retain actor-recovery proof and existing persistence formats. Do not call it disk/process proof or an archive feature. |
| Persisted results precede live session effects. | The session pipeline has one active streamed consumer. `SubAgentActor` keeps each child tool result in a private local envelope. | Retain streamed commit order. Keep each child tool result with its receipt and authorization facts. Do not treat this local envelope as a parent wake. |
| Generated histories expose omissions across different result orders. | `ActiveToolBatchTracker` has a small state and a direct test seam. | Run the Accordant/CsCheck component pilot in the current refactor. Keep actor tests for journal and provider behavior. Require generated transitions, replay, and a production negative control. |

Classify a mismatch as a product defect, test/adapter defect, or unresolved specification rule.
Do not freeze a suspected bug merely because the old actor exhibits it.
Stop only the affected change when intended behavior remains unresolved.
The user decides changed authority, accepted result order, recovery guarantees, or loop policy.
The root resolves ordinary fixture design, test selection, and source organization within the accepted contract.
The pilot tests result order, task completion, duplicate results, and batch reset.
Delivery retry remains outside this pilot.

## Historical child completion decision

The user requires each child result to wake its parent, as an ordinary tool result does.
A status event alone does not satisfy this requirement.
The refactor must prefer one completion mechanism over a separate child-result path through generic user input.
It must preserve each result, its original authority, and its durable identity.
It must prevent duplicate effects and duplicate model calls.

The private result envelope only carries tool results across the `SubAgentActor` mailbox.
It does not wake the parent session after a background child run completes.
That wake contract remains separate work.

PR #2421 at `2494a01eac5eb9310dee0c53577b695ca386de93` currently requires a terminal result to wait behind the active parent turn.
That rule conflicts with the new decision.
Its source records completion and emits status before it admits the result into a later parent turn.
A held-parent test expects the model call count to stay unchanged until the active work finishes.
These are source and test-assertion findings; this review did not execute that branch.

Astra will design the completion contract and identify mechanisms that can disappear.
The root will review that design before implementation.
Use the OpenSpec skills to amend the conflicting child-run requirement in the branch that owns it.
Keep this behavior change separate from the current tool-workflow transfer.
Do not merge the unrelated draft stack solely to make this refactor easier.

## Delivery

### PR 1: establish the tool-result baseline

A separate test author extends existing fixtures. The product author does not author these tests.
Run existing affected tests first. Add only the completion-order and result-association assertions that inspection finds missing.
Reuse task gates, subscribers, `ReceivedMessages`, and per-call execution counts. No broad harness rewrite or mandatory new package.
Keep the actor unchanged. Root review must accept the tests and evidence before PR publication.

Repeatable procedure:

1. Restore with the repository-pinned SDK. Create a fresh test system and owned temporary directory for each case.
2. Join with `OutputFilter.Full`; await `SessionJoined`; submit input and await `CommandAck`.
3. Await fake call-entry signals. Release the selected call order and collect correlated outputs and provider requests.
4. Await final completion. Check exact results, invocation counts, one continuation, and absence of unauthorized execution.
5. Retain failures with arguments and event order. Stop the actor system, then remove every owned temporary file.
6. Demonstrate a relevant negative control in an isolated copy, such as premature continuation or an omitted result.
7. Confirm the unchanged assertion rejects that fault. Restore the original product code and rerun the unchanged tests. A compile failure does not count.

Run this focused command before the full actor project:

```bash
dotnet test src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj --filter 'FullyQualifiedName~ToolExecutionIntegrationTests|FullyQualifiedName~ToolBatchHistoryWedgeTests|FullyQualifiedName~ToolCycleUserInputTests|FullyQualifiedName~ApprovalRehydrationTests|FullyQualifiedName~ApprovalTurnBoundaryTests|FullyQualifiedName~SessionToolExecutionPipelineTests'
```

Then run the full actor project, required Slopwatch/header checks, and required PR checks.
Record revision, exact commands, discovery/execution counts, results, and cleanup. Zero tests or skipped prerequisites do not pass.
A test-only rollback removes the added tests without changing product state.
After the native stack group merge, update later branches from the actual `dev`; rerun the baseline if upstream changed affected behavior.

### PR 2: transfer outcome acceptance and batch continuation

Start in a separate branch after the baseline passes and root review accepts the concrete owner API.
Publish PR 2 against the PR 1 branch after root review and local checks. Verify the combined stack.
Verify the combined stack and hold its merge until every member passes the required checks.
A different product author removes duplicated outcome processing and moves batch completion decisions with their state.
Reuse `ActiveToolBatchTracker`, `TurnStateTracker`, and `WorkingContextUpdater`.
Absorb the media list into the batch tracker and delete `ModelInputMediaBuffer`.
Do not create one wrapper per field or a generic effect interpreter.

Keep `_activeToolBatch` as the actor's batch reference. Move `_mediaBuffer` state and its result, completion, and reset rules into that tracker.
Keep `_turnState` as the existing rule owner; expose its current budget outcome through one continuation decision path.
Keep durable `_state`, admitted `TurnContext`, persistence callbacks, mailbox/timer APIs, and task cancellation in the actor.
Do not transfer approval authority, memory policy, or loop/cycle policy as incidental cleanup.
The domain owner selects the next batch action. The actor executes journal, output, model, and phase operations at the existing boundaries.

The current component API follows this sequence. This schematic omits logs and external effects:

```text
Before:
  actor interprets receipts twice
  actor mutates batch/media state
  actor repeats budget, buffer, and compaction branches

After:
  state = batch.ApplyRecordedResult(state, committedResult)
  context = WorkingContextUpdater.AcceptToolResult(context, message, receipt)
  completion = batch.CompleteIfReady(state, turnState, limit)
  if completion is Pending: return
  actor adopts state and drains admitted input
  next = completion.ChooseNext(newInput, compactionDue)
  actor executes ContinueModel | ContinueWithoutTools | RequestCompaction
```

The owner must decide the action. Passing mutable collections back for actor policy branches fails the ownership objective.
Preserve the existing precedence of new input, budget decisions, and compaction.
If these decisions cannot transfer safely without the recurrence work, extract common outcome acceptance first and record the remaining boundary explicitly.
Do not claim that narrower result completes the session refactor.

Retain the same actor assertions and add direct owner tests through the independent test author.
Remove the aggregate main-session handler: the sole production batch constructor selects streamed results, and synthetic cycle correction uses streamed messages.
Keep the child completion envelope private to `SubAgentActor`; it carries the existing per-call result records.
Replace direct pipeline-test aggregate mode with assertions on streamed replies.
Verify those retained callers. Preserve the live session commit boundary; do not normalize journal semantics.
Root review checks source ownership, preserved effects, separate test authorship, and required checks before PR delivery.
No schema migration is planned; rollback is a source revert. Do not execute both production paths to compare real effects.
Delete the replaced branches in the same PR. Keep one active path per ingress.

Measure against the merged PR 1 baseline with `git diff --numstat` and the same source-count procedure.
Report actor gross removal, actor additions, replacement production additions, repository production net, tests, and documentation separately.
Report fields moved, fields eliminated, retained state, and new owner references. Dependency grouping does not count as state elimination.
Seek deletion before new structure. Each slice must identify duplicate facts, obsolete messages, redundant checks, and unnecessary dependencies.
Prefer one existing identity or lifecycle over a second counter, queue, or flag.
Delete a mechanism only after tests prove that its required behavior remains intact.
Root review rejects a cosmetic method move or repository growth without a justified ownership benefit.
No line quota can override behavior. The broader objective remains responsibility transfer across thousands of actor lines where evidence supports it.

### Later scope: provisional

Use this order after the first two PRs. Keep exact APIs provisional until each boundary receives source review.

3. Complete coherent tool state and safe continuation. Transfer remaining dispatch/resumption rules with their state. Recheck recurrence overlap against the current `dev`.
4. Extract context and request preparation. Preserve stale-generation rejection, authority, prompt inputs, and model request equivalence.
5. Consolidate terminal state changes in `SessionState`. Remove duplicate live and recovery reducers. Prove the recovered reminder ledger contract.
6. Encapsulate model and tool task lifetime. Remove duplicate busy and restart facts. Preserve phase effects and exact task-stop checks.
   Before a later turn-admission transfer, prove committed turn identity and actor restart replay. Those checks remain separate work.
7. Consolidate and measure. Remove obsolete paths, duplicate state, temporary adapters, and unjustified contracts. Compare the whole production footprint.
8. Add the bounded Accordant/CsCheck tool-batch pilot as the final support PR after the lifecycle and obsolete-path PRs.
   Keep one generated component test. Verify it on the combined stack before publication.

The current Step 7 deletion scope removes the old memory extractor and its compaction reply path.
Checkpoint curation remains the active memory path. The session pipeline retains streamed tool replies only.
`SubAgentActor` keeps a private result envelope with per-call records instead of parallel aggregate maps.
This local envelope does not implement the separate background-child wake contract.
Direct pipeline tests must assert streamed result content, metadata, and batch completion.

Each step needs a narrow baseline, an independent test author, product changes, verification, root approval, and PR publication.
Label each PR with `sessions` and `refactoring`. Add `tests`, `.NET`, and other existing labels that match its scope.
Stack ready PRs on `feature/*` branches. Name the base PR in each description and verify the combined stack.
Wait until every PR in the stack passes its checks on the exact head.
Then use a verified group merge operation for the complete stack. Do not merge individual PRs.
Reassess conflicts and rerun affected checks before the group merge.
Retain public behavioral assertions; add direct owner tests after each boundary becomes concrete.
Do not build one model of all actor state. Create each stack branch when its implementation starts.
The large actor-reduction ranges remain targets. Actor relocation does not establish total-source reduction.
Report repository production net after replacement code and adapters before any claim of substantial simplification.

### Next slice: one terminal state reducer

The current actor repeats terminal state changes for text replies, routed replies, and journal recovery.
The recovery branch can omit the completed reminder ledger when it restores the user message first.
An independent baseline test proves this gap for an accepted `InputAdmitted` event with a null `SourceMessageId`.
No source evidence confirms that a historic producer emitted that event shape.
The current reminder producer uses one stable key for `MessageId` and `ReminderId`.
Source-key deduplication protects a short replay while that key remains in the recent-key set.

Extend `SessionState`, which already owns history, durable inputs, and completed source records.
Use one reducer to close the exact input IDs, append each required message once, and update the source records.
Delete the duplicate actor state blocks after independent live and recovery tests pass.
Keep actor output, task release, and journal order at their current boundaries.
Do not replace the live reducer with the current `Apply(TurnRecorded)` without proof; that method appends the user message again.

The memory review rejects the proposed `SessionMemoryAcceptance` wrapper.
Existing memory components already own proposal validation, checkpoint construction, child finding review, and observer state.
A new wrapper adds another call layer without removing a responsibility or duplicate rule.

The activity review also rejects one collection for queued inputs, closed inputs, source reservations, and history replay.
Those facts have different lifetimes. Combining them requires more status rules and does not remove the facts.
Keep the current typed source reservations until a concrete replacement proves their full contract.
Review the committed turn identity and lost actor restart replay marker as separate behavior corrections.
Actor restart replay does not provide process crash recovery.
The broader lifecycle transfer remains required; these corrections do not complete the refactor.

## Historical status and handoff

[PR #2440](https://github.com/netclaw-dev/netclaw/pull/2440) merged into the Stage 1 feature branch before its hosted checks completed.
That historical merge did not establish hosted acceptance at the time.
Do not enable automatic merge on an unprotected feature base. It can merge immediately despite active checks.
Wait for every stack head to pass its checks, then merge the stack as a group.
[PR #2439](https://github.com/netclaw-dev/netclaw/pull/2439) later merged the combined first two steps into `dev` as `32348bfb`.
The Stage 2 tool-result extraction removed 273 actor lines and 185 total production lines.
[PR #2441](https://github.com/netclaw-dev/netclaw/pull/2441) merged Stage 3 into `dev` as `123daad6959df84460508fd89826c4404b0956e8`.
All 30 hosted checks passed before that merge. One post-merge check skipped.
The Stage 3 workflow change removed 570 actor lines and three fields. It added 90 total production lines.
The merged Stage 3 tree is the Stage 4 base at `83a86ef1`.

Stage 4 moved prompt, recall, snapshot, and model request preparation into `SessionModelContext`.
It removed 288 actor lines and ten actor fields. This slice added 39 total production lines.
The full actor suite passed 5,749 cases and retained 41 skips.
The stale-reply negative control failed at its expected assertion, then passed after source restoration.
The focused mutation gate killed all three selected mutants.
[PR #2442](https://github.com/netclaw-dev/netclaw/pull/2442) merged Stage 4 into `dev` as `45a3d05a`.

Stage 5 extends the existing `SessionState` reducer at local source commit `8e1925d6`.
Both live terminal callbacks and recovery now use its shared history, input-closure, and source-record rules.
The actor loses 39 lines. Total production source loses 36 lines. No actor field moves or disappears in this slice.
The cumulative actor reduction reaches 1,170 lines. Total production source falls by 92 lines before the lifecycle draft.
The unchanged reminder regression passes after the repair. Its baseline failed at the expected assertion.
The focused suite passes 53 cases. The full Actors suite passes 5,754 cases and retains 41 existing skips.
The two history-choice mutants and three prior session-reliability mutants were killed in the final focused gate.
The unchanged reducer theory failed both rows after a detached negative control removed the reminder ledger update.
It passed both rows after source restoration, and the restored source and test hashes matched this worktree.
Final Slopwatch found zero issues. Copyright headers and whitespace checks passed.
These results describe the local Stage 5 checkpoint before the later group merge.
Reminder records remain absent from snapshots under the existing contract. This repair does not promise deduplication across every snapshot boundary.

Stage 6 starts in a separate worktree from the Stage 5 production commit.
One private owner receives the existing task, cancellation, stop, and watchdog state.
The actor retains phase decisions and restart policy. The draft deletes the duplicate busy and restart-request facts.
The two drain methods remain separate until their phase-specific effects have direct proof.
The source trace suggests a possible batch-completion and restart-stop interaction, but no reachable failure was reproduced.
This structural slice preserves that behavior. Any repair requires a failing baseline and separate review.
The lifecycle draft removes 133 actor lines and adds 110 total production lines.
The existing member collector counted 73 actor instance fields before the transfer and 63 afterward.
Each source version also has eight static fields and three constants, for 84 and 74 total declarations.
Nine resource fields moved into the owner. Two duplicate facts disappeared. The actor gained one owner field.
The total production source still grows in this slice, so this slice does not satisfy the total-source reduction goal.
The root reviewed the production draft and direct tests, then published PR #2445.

[PR #2443](https://github.com/netclaw-dev/netclaw/pull/2443), [PR #2445](https://github.com/netclaw-dev/netclaw/pull/2445), [PR #2446](https://github.com/netclaw-dev/netclaw/pull/2446), and [PR #2447](https://github.com/netclaw-dev/netclaw/pull/2447) merged as one native group.
The later stop policy work uses that combined source.
The independent tests passed 68 focused cases after a test probe fix.
The ToolLoop compaction class passed three cases on the Stage 5 parent.
The full Actors suite passed 5,764 cases and retained 41 platform skips.
The final owner-focused run passed 12 cases on `ecac1c7e`.
It includes one shared xUnit2/xUnit3 model stop identity case and 11 existing owner cases.
The full-suite run preceded the shared-source test and mutation gate edits.
An isolated negative control removed the model stop call-ID check; the unchanged stale-stop test failed at its expected assertion.
The six-target session reliability gate killed all six selected mutants in 1 minute 58 seconds.
Its selected model stop guard mutant died. The control source returned to its exact hash.
Slopwatch, copyright headers, and whitespace checks passed after the final test and gate edits.
No runtime path in this slice enters Compacting directly from Ready.
The actor proof covers Processing to Compacting to Processing, plus exit to Ready and passivation.
The possible batch-completion and restart-stop race remains outside this structural transfer.
The obsolete-path parent is committed at `abc1e295`. The bounded Accordant pilot ran on that combined tree.

The independent test author owns baseline evidence. The product author owns extraction. The root owns review and each delivery decision.
The Accordant/CsCheck pilot now covers `ActiveToolBatchTracker` in a separate test worktree.
The combined branch starts at obsolete-path commit `abc1e295`.
The focused local run passed 1,000 generated cases and 5,563 operations after the diagnostic correction.
An unsafe production readiness mutation failed on a replayable two-step trace.
The original production source passed after restoration. The full Actors suite passed 5,760 cases.
Actor integration proof remains separate from the component model.
The order-count diagnostic now requires both a result and later task completion in one batch.
The combined local run passed. Hosted checks remain required on the published stack head.
Persistence or authority changes remain explicit product decisions.
Use the Systematize plan/verify guidance and Code With Aaron test/refactor/review guidance during execution.
If those skills are unavailable, report the gap and preserve this plan's proof and separate-authorship requirements.

### Session stop policy group

The group started from combined local commit `bc27ea24` in a separate worktree.
It did not extend the four-PR group before that group merged.
An independent baseline on `8fd1171b` proved a late-reap defect.
After an input aborted idle passivation, the reap event committed, but the retired stop attempt armed a final timer.
The timer reached `Processing` as a dead letter. A later idle passivation then failed to terminate.
The repair lets idle sessions release actor resources after the valid late reap commit.

Commit `322b9575` is the separate behavior correction. It clears the retired attempt's reap prerequisite on abort.
The reap operation still commits its journal event after abort. The unchanged independent regression passed on that commit.
The evidence receipt records the correction test source hash.

The subsequent transfer gives `SessionExecutionLifetime` the complete restart and passivation policy.
The actor retains journal writes, job-manager lookup, output, lifecycle notification, and `Context.Stop`.
`SessionPhaseMachine` remains the sole phase owner. The owner receives the current phase for each decision.
The owner retains the exact model call and tool task identities. It keeps a reap operation separate from the passivation attempt.
The attempt milestone replaces the deferred-reap, final-grace, and completion flags.
The owner also validates the complete stored authority for restart reminders and resumed inputs.

The transfer removes seven direct actor fields and adds no actor field.
It removes one underlying terminal boolean because the final milestone already records that fact.
Against correction commit `322b9575`, the actor changes from 4,119 to 3,928 lines: 191 fewer lines.
The owner changes from 243 to 546 lines: 303 more lines.
The total production source grows by 112 lines across those two changed production files.
The actor diff has 96 added and 287 removed lines. The owner diff has 304 added and one removed line.
These counts exclude tests, mutation configuration, and documentation.

An initial transfer-focused run passed 40 cases before the new independent tests entered the worktree.
The final focused run passed 64 cases with no failures or skips.
It includes the independent late-reap, superseded-reap, owner-policy, approval-recovery, restart, and passivation tests.
The raw log and TRX are under `planning/netclaw/session-refactor/review/evidence/session-stop-transfer/` outside this repository.
The focused mutation gate killed all seven selected mutants in 108.47 seconds.
The new target changed the reap commit marker; the unchanged owner test killed mutant `7949`.
The raw report is under `planning/netclaw/session-refactor/review/evidence/session-stop-mutation/` outside this repository.
The full Actors suite passed 5,766 cases, with 41 platform skips and no failures.
The full run used the current combined source after the focused run and mutation config update.
Its TRX and raw log are under `planning/netclaw/session-refactor/review/evidence/session-stop-transfer/` outside this repository.
Slopwatch found zero issues. The copyright header and whitespace checks passed.
[PR #2449](https://github.com/netclaw-dev/netclaw/pull/2449) is open for the stop policy group.

### Session input owner group

`SessionInputFlow` now owns the live input queue, selected IDs, source reservations, current authority, telemetry, and continuation facts.
The actor retains journal writes, phase changes, history effects, model calls, and output.
The live turn uses the `TurnId` and authority from its committed `InputAdmitted` record.
At that time, the owner drained all buffered messages in one ordered follow-up batch, including mixed Personal and Public sources.
That change preserved the authority policy of that time. It did not repair mixed-authority tool exposure or child-result wakeup.
The one-authority group below replaces this batch rule.

The transfer removes thirteen direct actor fields and adds one owner reference.
Against stop head `73b4ba2f`, the actor falls from 3,928 to 3,768 lines, a reduction of 160.
The new owner has 382 lines. Total Actors production C# grows by 222 lines.
The separate next slice deletes duplicate phase registrations and the parent's duplicate media result field.

An independent identity baseline failed when the live `TurnId` differed from the admitted event.
The correction and one ordered mixed-authority batch passed focused actor tests.
The independent owner tests passed two cases; a fresh-turn mutation failed at the intended assertion.
A first combined run exposed an Akka serializer error for a compiler-generated speaker-ID collection.
The repaired journal boundary stores a concrete array. The repaired focused suite passed 104 cases.
The full Actors suite passed 5,774 cases, skipped 41 platform cases, and failed zero.
These tests observe admission after acknowledgment; they do not prove every callback race.
The focused session reliability gate killed all eight selected mutants in 125.78 seconds.
Slopwatch found zero issues. The copyright header and whitespace checks passed.
Raw results are under `planning/netclaw/session-refactor/review/evidence/input-flow-proof/` outside this repository.

### One authority per turn group

The operator decided that a turn has one authority, fixed when the turn starts.
Inputs from different requesters never share a turn. An input from another requester waits for its own turn.
`SessionInputFlow.DrainBufferedBatch` now takes only the leading buffered inputs of one requester (`TurnContext.HasSameRequester`).
After a turn, the flow adopts the recorded authority of the first buffered input. Inside a turn, only the turn's requester joins.
The actor derives trust and the prompt audience for an adopted authority before the model call.
Automated input (principal `VerifiedAutomation` or `SystemProcess`) never abandons an open approval. It waits in the buffer.

The pinned mixed case in `LlmSessionIntegrationTests.Buffered_messages_are_batched_into_follow_up_LLM_call` now expects three follow-up turns.
`SessionInputFlowAccordantTests` checks order, one admission per source key, no starvation, closure, one requester per batch, and the batch authority of each model call.
`RecoveredApprovalAbandonmentTests` and `AutomatedInputApprovalWaitTests` check that a reminder keeps an open approval and runs later.
The focused gate `./scripts/run-input-one-authority-mutations.sh` kills 8 mutants with `SessionInputFlowRequesterTests`.
