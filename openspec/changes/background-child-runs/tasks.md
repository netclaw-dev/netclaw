## 1. Freeze prerequisites and independent proof fixtures

- [x] 1.1 Rebase on PR 1 and reconcile its frozen types and deltas; verify the combined recurrence contract remains unchanged.
- [ ] 1.2 Record the owner's pending capacity decision before dependent runtime edits; verify the selected admission and held-child model-response clauses are explicit.
- [x] 1.3 Have an independent test author derive lifecycle assertions from the delta; verify the author lists paired controls and deliberate faulty variants.
- [ ] 1.4 Extend existing actor fixtures with start, provider, dispatch, terminal, and persistence barriers; verify assertions use acknowledgements rather than sleeps.
- [x] 1.5 Capture a pre-change journal/snapshot fixture with conversation and parent approval state; verify the fixture predates new lifecycle records.

Preparation evidence:

- The branch includes PR 1 at `893435238`. Its existing runtime types remain unchanged.
- Design section 7 assigns retained parent detector evidence to the planned child run ledger.
- An independent review derives sixteen assertion groups, paired controls, and named faults from the deltas.
- The [approved plan](../../../.systematize/plans/background-subagents/plan.html) owns the adversarial method and acceptance gates.
- The original `2e6bc4f` capture already supplies conversation, original requester, and parent approval facts.
- Reuse `src/Netclaw.Actors.Tests/Sessions/Fixtures/LegacyPersistence/legacy-session-v0.json`; do not recapture or infer child records.
- Its SHA-256 is `bf400d15ea1541d1a59b8be238705c99eb7f489413c863d93b17976ab1cfc94b`.
- These preparation tasks establish no background runtime or provider-capacity pass.

## 2. Durable acceptance and independent lifetime

- [ ] 2.1 Extend the owner-session seam with typed accepted-run operations and reuse `ChildRunScope.Authority`; verify no parallel requester or path-root inputs exist.
- [ ] 2.2 Stamp tool and routed activation keys before asynchronous setup; verify retries preserve original call/input IDs and conflicting digests fail.
- [ ] 2.3 Add framework-owned acceptance records and snapshot conversion; verify round-trip and journal replay preserve the same key, context, and run ID.
- [ ] 2.4 Persist acceptance before execution and acknowledgement; fault the write and verify zero child requests or task effects.
- [ ] 2.5 Deduplicate concurrent and recovered starts; lose the accepted response and verify one child plus the same returned run ID.
- [ ] 2.6 Separate accepted-run lifetime from the start token; cancel that token after commit and verify the child remains controllable.
- [ ] 2.7 Report post-acceptance creation failure through the accepted run; verify one durable terminal failure and no false rejection/restart.

## 3. Terminal receipt and attributed continuation

- [ ] 3.1 Add child terminal acknowledgement and close task dispatch before terminal output; verify the child retains its result until durable acknowledgement.
- [ ] 3.2 Authenticate terminal sender and generation; replay stale and duplicate results and verify one committed winner.
- [ ] 3.3 Persist terminal receipt before enrichment; fail persistence and verify no successful acknowledgement, delivery claim, or model request.
- [ ] 3.4 Move result enrichment to recorded terminal handling; fail enrichment and verify the original outcome survives with an explicit warning.
- [ ] 3.5 Atomically persist delivery dedup and continuation input; crash before/after admission and verify one pending input without child replay.
- [ ] 3.6 Materialize a fresh attributed provider tool-call/result pair; inspect actual messages and verify the original start call receives no second result.
- [ ] 3.7 Preserve original authority and compatible journal-order queues; verify another speaker and active compaction cannot change result authority.
- [ ] 3.8 Retain and refresh parent detector checkpoints in the child run ledger. Verify committed start settlement and direct activation without fabricated receipts. Verify late results after fresh input, sibling checkpoint updates, distinct-task separation, receipt failure, and recovery. Preserve current parent directory/project/branch.
- [ ] 3.9 Keep failed parent continuations visible; verify replay does not admit completed input or automatically restart external effects.

## 4. Every routed start and deferred parent controls

- [ ] 4.1 Convert `spawn_agent` to the canonical acceptance JSON. Verify identical output/model bodies, valid owner identifiers, and initial `Accepted` state. Hold the child and verify parent replies before release. Reject malformed or uncommitted acceptance.
- [ ] 4.2 Convert routed `skill_load`; verify target validation, content scan, overlay semantics, and explicit failures remain intact.
- [ ] 4.3 Convert slash, scheduled slash, and reminder skill activation; verify each returns acceptance and retains its original occurrence/call correlation.
- [ ] 4.4 Preserve inline activation and no-inline-fallback failures; verify unknown/internal targets create neither a child nor inline work.
- [ ] 4.5 Add Deferred `check_agent_run(run_id, cancel=false)` through normal non-shell policy; verify explicit load, unchanged core, and direct ownership checks.
- [ ] 4.6 Deny child, cross-session, and wrong-requester controls; verify no target details and keep the valid parent control operational.
- [ ] 4.7 Add bounded recorded-run context with the exact control name; verify it cannot claim unobserved live provider state or expose hidden results.

## 5. Cancellation, partial evidence, and approval prompts

- [ ] 5.1 Persist cancellation admission and acknowledge local dispatch closure separately; hold a dispatch worker and verify the acknowledgement cannot precede closure.
- [ ] 5.2 Close queued model/tool dispatch and exact approval retries; release stale callbacks and verify no new task operation after confirmed cutover.
- [ ] 5.3 Commit bounded checkpoints after completed child rounds; verify confirmed receipts and paths survive a later cancel without a new model response.
- [ ] 5.4 Add five-second framework-only finalization with a separate token; verify report writes stay run-local and no task tool or project edit occurs.
- [ ] 5.5 Fault report writes and expire controlled finalization timers; verify retained checkpoint evidence, explicit reason, and one cancelled result.
- [ ] 5.6 Extend cancelled completion with confirmed activity; verify `Success=false`, `Failed`, and `CancelledByParent` survive wire conversion.
- [ ] 5.7 Exercise both durable completion/cancel orders; verify the first permitted terminal winner and accurate unconfirmed external-effect reports.
- [ ] 5.8 Retain original approval prompt correlation beyond the start call; verify an original eligible answer resumes only the exact live child retry.
- [ ] 5.9 Persist child prompt lifecycle facts and settle them on cancel/loss; verify late answers create no tool execution or authorization grant.
- [ ] 5.10 Keep child prompt waits independent of later ordinary parent input; verify that input neither adopts requester authority nor abandons the child prompt.

## 6. Passivation, restart, and compatibility

- [ ] 6.1 Defer idle passivation for live children and pending result admission; verify normal idle behavior resumes when those obligations end.
- [ ] 6.2 Retain shell-job reap behavior as a nearby control; run its existing integration cases and verify no child state enters shell-job records.
- [ ] 6.3 Cancel children during coordinated drain with bounded finalization; verify parent snapshot retains terminal and prompt disposition.
- [ ] 6.4 Recover accepted children as explicit `Lost` without relaunch; verify committed cancellation remains cancelled and stale success cannot win.
- [ ] 6.5 Crash at acceptance, terminal persistence, enrichment, delivery preparation, and admission; verify no duplicate child or completed side-effect replay.
- [ ] 6.6 Load the captured pre-change fixture. Verify retained history, parent prompts, and an explicit empty child ledger. Capture a baseline SQLite storage binding and marker through the real resolver. Verify the candidate preserves that row and marker. The journal fixture alone supplies no storage-binding proof.
- [ ] 6.7 Probe prior-reader compatibility against new events; verify rollback instructions require backup restoration when compatibility fails.

## 7. Integrated health, evals, and sensitive fault controls

- [ ] 7.1 Extend the existing background relay with explicit parent/child request IDs and barriers; verify setup/sidecar calls cannot consume child script stages.
- [ ] 7.2 Add targeted Subagents and background cases for acceptance, routed starts, controls, cancellation, prompts, and recovery; verify actual dispatch and artifact evidence.
- [ ] 7.3 Preserve queued-grant revocation, shell lifecycle, and PR 1 long-task controls; verify the combined candidate passes those unchanged boundaries.
- [ ] 7.4 Exercise the chosen admission contract under saturation; verify bounded queue state, explicit outcomes, and locally responsive status/cancel.
- [ ] 7.5 Hold a child on the selected real provider and request a parent reply; verify that reply precedes child release in five critical-case trials.
- [ ] 7.6 Make provider output ignore cancellation or never finish; verify framework settlement without another model response and report remote-computation limits.
- [ ] 7.7 Demonstrate rejection of duplicate start, early terminal acknowledgement, late dispatch, stale approval prompt, and delivery-reset mutants in isolation.
- [ ] 7.8 Keep any new focused mutation target narrow and inside the documented CI budget; verify expected mutants execute and none survives.
- [ ] 7.9 Require an independent verifier to inspect integrated results and assertions; verify no skipped cases, weakened gates, or author-only pass claims remain.

## 8. Docs, operational guidance, and handoff

- [x] 8.1 Rename the glossary entry to approval prompt with a legacy-anchor alias; verify old links still resolve and exact code identifiers remain unchanged.
- [ ] 8.2 Update `SPEC-002`, `SPEC-016`, and the authorization architecture document; verify they describe implemented ownership and cancellation order.
- [ ] 8.3 Add minimum runtime `AGENTS.md` guidance; verify it describes background acceptance, deferred control, partial result review, and cancel/recreate only.
- [ ] 8.4 Update `subagent-authoring` and `netclaw-operations` versions; verify instructions advertise no deferred messages or restored tool ceilings.
- [ ] 8.5 Run targeted identity/tool/skill evals for those changed instructions; verify selected resource and control receipts satisfy the actual task.
- [ ] 8.6 Run applicable builds, required tests, Slopwatch, and copyright-header verification; retain exact candidate and meaningful test counts.
- [ ] 8.7 Validate the OpenSpec change strictly and sync implemented deltas through the appropriate skill; verify the PRD and active plan trace to the final contract.
- [ ] 8.8 Prepare rollback and evidence handoff for the combined PR; verify no merge, rollout, publication, or database replacement is implied by local passes.
