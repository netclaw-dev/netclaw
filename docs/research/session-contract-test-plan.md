# Session contract tests and the first substantial extraction

**Recommendation:** strengthen the narrow tool-result baseline, then extract tool-result acceptance and batch continuation under the same tests.
This protects user-visible behavior while the actor transfers real state and decisions.
Do not wait for a complete actor model or an Accordant framework project.
Installed agent skills do not commit the repository to Accordant adoption.

Baseline: `4753b7682c353dc3b7e3932f458124052dbd6adb` on `dev`.
The status section separates completed proof from proposed work.
The user authorizes implementation and verification. The root approves each result before PR publication. The stack needs exact-head checks before one group merge.
Each PR must preserve accepted behavior. A behavior change needs a separate decision.

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

If Accordant fits, let its generator choose sequences, repetitions, completion orders, and valid/invalid answers.
Use finite domains: two call IDs, one unknown ID, two requester IDs, success/failure, and allow/deny.
Choose explicit depth and operation bounds that reach a complete two-call turn and repeated invalid answers.
Inspect generated coverage; do not rely on the default depth or state coverage to exercise every transition.
Keep exact operation arguments, traces, bounds, and observations for replay.
An operation trace needs task gates and acknowledgements to reproduce actor timing.

Use Arrange/Act/Assert with xUnit. Share assertions through theories when the tested behavior is the same.
The test adapter may change internally after extraction; accepted oracle rules and external assertions must remain unchanged.

## Decisions and proof limits

| Intended behavior and benefit | Evidence and prior-design question | Decision and proof |
| --- | --- | --- |
| Safe continuation prevents incomplete provider history and duplicate work. | Existing tests prove selected paths; source repeats outcome/continuation work. Does the split still serve a necessary delivery distinction? | Keep ingress-specific commit order. Consolidate domain rules. Prove both completion orders and partial failure. |
| Durable approval resumption lets the UI safely resume interrupted work. | The test base uses in-memory journal/snapshots. Does this prove process durability? No. | Retain actor-recovery proof and existing persistence formats. Do not call it disk/process proof or an archive feature. |
| Persisted results precede live session effects. | The session pipeline has one active streamed consumer. `SubAgentActor` has a separate aggregate result path. | Retain streamed commit order and the shared child aggregate message. Remove the unused session pipeline aggregate mode. |
| Generated histories expose omissions without a framework detour. | The actor harness already exists; Accordant permits partial specifications. Does its adapter add useful cases cheaply? | Adopt only with compact state, nonempty cases, real observations, understandable failures, replay, and relevant negative controls. Otherwise use xUnit and defer it. |

Classify a mismatch as a product defect, test/adapter defect, or unresolved specification rule.
Do not freeze a suspected bug merely because the old actor exhibits it.
Stop only the affected change when intended behavior remains unresolved.
The user decides changed authority, accepted result order, recovery guarantees, or loop policy.
The root resolves ordinary fixture design, test selection, and source organization within the accepted contract.
Delivery retry remains an optional small framework experiment. Its small state does not justify delaying this extraction.

## Child completion: explicit product decision

The user requires each child result to wake its parent, as an ordinary tool result does.
A status event alone does not satisfy this requirement.
The refactor must prefer one completion mechanism over a separate child-result path through generic user input.
It must preserve each result, its original authority, and its durable identity.
It must prevent duplicate effects and duplicate model calls.

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
After merge, update the extraction branch from the actual `dev`; rerun the baseline if upstream changed affected behavior.

### PR 2: transfer outcome acceptance and batch continuation

Start in a separate branch after the baseline passes and root review accepts the concrete owner API.
Publish PR 2 against the PR 1 branch after root review and local checks. Verify the combined stack.
After PR 1 merges, rebase onto its actual merge result and rerun affected checks.
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
Retain the shared `ToolExecutionCompleted` type: `SubAgentActor` sends it to itself.
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

The current Step 7 deletion scope removes the old memory extractor and its compaction reply path.
Checkpoint curation remains the active memory path. The session pipeline retains streamed tool replies only.
`SubAgentActor` retains its shared `ToolExecutionCompleted` message and its own aggregate child path.
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

## Status and handoff

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
These results are local Stage 5 evidence. Stage 5 has no hosted CI result yet.
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

[PR #2443](https://github.com/netclaw-dev/netclaw/pull/2443) is the Stage 5 parent.
The lifecycle worktree used `86452744` for the full actor check.
After PR #2442 merged, GitHub rebased PR #2443 to `ecac1c7e`.
The root verified that both Stage 5 heads have the same tree, `471f8be3`.
The lifecycle worktree then moved to `ecac1c7e` without a source change.
Keep PR #2443 and later stack PRs open until every stack head passes its checks.
[PR #2445](https://github.com/netclaw-dev/netclaw/pull/2445) is open at `8fd1171bd605cd07edeb79d9b9249d3336d52b49`.
Its base is PR #2443. GitHub native stack `2444` contains both open PRs.
Hosted checks still ran at this review. Merge the complete stack with one atomic native group request after every PR passes.
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
The obsolete-path product and independent test authors now prepare combined tests. The bounded Accordant pilot follows.

The independent test author owns baseline evidence. The product author owns extraction. The root owns review and each delivery decision.
Accordant fit and exact owner API remain engineering checks. Persistence or authority changes remain explicit product decisions.
Use the Systematize plan/verify guidance and Code With Aaron test/refactor/review guidance during execution.
If those skills are unavailable, report the gap and preserve this plan's proof and separate-authorship requirements.
