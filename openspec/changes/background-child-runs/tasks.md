## 1. Freeze prerequisites and independent proof fixtures

- [x] 1.1 Rebase on PR 1 and reconcile its frozen types and deltas; verify the combined recurrence contract includes the confirmed model-owned recovery boundary.
- [x] 1.2 Record the confirmed backend-owned capacity boundary. Remove provider-slot coordination and the pending approval gate. Verify independent request and local control clauses.
- [x] 1.3 Have an independent test author derive lifecycle assertions from the delta; verify the author lists paired controls and deliberate faulty variants.
- [x] 1.4 Extend existing actor fixtures with start, provider, dispatch, terminal, and persistence barriers; verify assertions use acknowledgements rather than sleeps.
- [x] 1.5 Capture a pre-change journal/snapshot fixture with conversation and parent approval state; verify the fixture predates new lifecycle records.

Preparation evidence:

- The branch inherits PR 1. This contract reconciliation leaves its existing runtime types unchanged.
- Design section 7 assigns retained parent detector evidence to the planned child run ledger.
- An independent review derives sixteen assertion groups, paired controls, and named faults from the deltas.
- The [approved plan](../../../.systematize/plans/background-subagents/plan.html) owns the adversarial method and acceptance gates.
- The original `2e6bc4f` capture already supplies conversation, original requester, and parent approval facts.
- Reuse `src/Netclaw.Actors.Tests/Sessions/Fixtures/LegacyPersistence/legacy-session-v0.json`; do not recapture or infer child records.
- Its SHA-256 is `bf400d15ea1541d1a59b8be238705c99eb7f489413c863d93b17976ab1cfc94b`.
- A separate baseline capture supplies one real SQLite storage binding and its neutral workspace marker.
- Reuse `src/Netclaw.Daemon.Tests/Gateway/Fixtures/LegacyPersistence/legacy-storage-binding-v0.json` with its bundled capture source and provenance.
- The original resolver creates the captured row. The consumer restores all four columns and the marker under a new home.
- The combined candidate passes 33 storage-resolver cases without failures or skips.
- Task 6.6 remains open for the new empty child ledger and the integrated lifecycle recovery proof.
- These preparation tasks establish no background runtime pass. The owner confirmed backend-owned capacity and model-owned recovery.

## 2. Durable acceptance and independent lifetime

- [x] 2.1 Extend the owner-session seam with typed accepted-run operations and reuse `ChildRunScope.Authority`; verify no parallel requester or path-root inputs exist.
- [x] 2.2 Stamp tool and routed activation keys before asynchronous setup; verify retries preserve original call/input IDs and conflicting digests fail.
- [x] 2.3 Add framework-owned acceptance records and snapshot conversion; verify round-trip and journal replay preserve the same key, context, and run ID.
- [x] 2.4 Persist acceptance before execution and acknowledgement; fault the write and verify zero child requests or task effects.
- [x] 2.5 Deduplicate concurrent and recovered starts; lose the accepted response and verify one child plus the same returned run ID.
- [x] 2.6 Separate accepted-run lifetime from the start token; cancel that token after commit and verify the child remains controllable.
- [x] 2.7 Report post-acceptance creation failure through the accepted run; verify one durable terminal failure and no false rejection/restart.

## 3. Terminal receipt and attributed continuation

- [x] 3.1 Add child terminal acknowledgement and close task dispatch before terminal output; verify the child retains its result until durable acknowledgement.
- [x] 3.2 Authenticate terminal sender and generation; replay stale and duplicate results and verify one committed winner.
- [x] 3.3 Persist terminal receipt before enrichment; fail persistence and verify no successful acknowledgement, delivery claim, or model request.
- [x] 3.4 Move result enrichment to recorded terminal handling; fail enrichment and verify the original outcome survives with an explicit warning.
- [x] 3.5 Atomically persist delivery dedup and continuation input; crash before/after admission and verify one pending input without child replay.
- [x] 3.6 Materialize a fresh attributed provider tool-call/result pair; inspect actual messages and verify the original start call receives no second result.
- [x] 3.7 Preserve original authority and compatible journal-order queues; verify another speaker and active compaction cannot change result authority.
- [x] 3.8 Retain and refresh parent detector checkpoints in the child run ledger. Verify committed start settlement and direct activation without fabricated receipts. Verify late results after fresh input, sibling checkpoint updates, distinct-task separation, receipt failure, and recovery. Preserve current parent directory/project/branch.
- [x] 3.9 Keep failed parent continuations visible. Verify committed input is not readmitted and the model receives retained loss facts before it chooses further work.

## 4. Every routed start and deferred parent controls

- [x] 4.1 Convert `spawn_agent` to the canonical acceptance JSON. Verify identical output/model bodies, valid owner identifiers, and initial `Accepted` state. Hold the child and verify parent replies before release. Reject malformed or uncommitted acceptance.
- [x] 4.2 Convert routed `skill_load`; verify target validation, content scan, overlay semantics, and explicit failures remain intact.
- [x] 4.3 Convert slash, scheduled slash, and reminder skill activation; verify each returns acceptance and retains its original occurrence/call correlation.
- [x] 4.4 Preserve inline activation and no-inline-fallback failures; verify unknown/internal targets create neither a child nor inline work.
- [x] 4.5 Add Deferred `check_agent_run(run_id, cancel=false)` through normal non-shell policy; verify explicit load, unchanged core, and direct ownership checks.
- [x] 4.6 Deny child, cross-session, and wrong-requester controls; verify no target details and keep the valid parent control operational.
- [x] 4.7 Add bounded recorded-run context with the exact control name; verify it cannot claim unobserved live provider state or expose hidden results.
- [x] 4.8 Return canonical live `log_path` and `artifact_directory` after owner authorization. Hold a real child, inspect the actual status JSON, and read its exact log with ordinary file tools. Verify foreign controls reveal no paths and returned paths cannot bypass file policy.

## 5. Cancellation, partial evidence, and approval prompts

- [x] 5.1 Persist cancellation admission and acknowledge local dispatch closure separately; hold a dispatch worker and verify the acknowledgement cannot precede closure.
- [x] 5.2 Close queued model/tool dispatch and exact approval retries; release stale callbacks and verify no new task operation after confirmed cutover.
- [x] 5.3 Commit bounded checkpoints after completed child rounds; verify confirmed receipts and paths survive a later cancel without a new model response.
- [x] 5.4 Add five-second framework-only finalization with a separate token; verify report writes stay run-local and no task tool or project edit occurs.
- [x] 5.5 Fault report writes and expire controlled finalization timers; verify retained checkpoint evidence, explicit reason, and one cancelled result.
- [x] 5.6 Extend cancelled completion with confirmed activity; verify `Success=false`, `Failed`, and `CancelledByParent` survive wire conversion.
- [x] 5.7 Exercise both durable completion/cancel orders; verify the first permitted terminal winner and accurate unconfirmed external-effect reports.
- [x] 5.8 Retain original approval prompt correlation beyond the start call; verify an original eligible answer resumes only the exact live child retry.
- [x] 5.9 Persist child prompt lifecycle facts and settle them on cancel/loss; verify late answers create no tool execution or authorization grant.
- [x] 5.10 Keep child prompt waits independent of later ordinary parent input; verify that input neither adopts requester authority nor abandons the child prompt.

## 6. Passivation, restart, and compatibility

- [x] 6.1 Defer idle passivation for live children and pending result admission; verify normal idle behavior resumes when those obligations end.
- [x] 6.2 Retain shell-job reap behavior as a nearby control; run its existing integration cases and verify no child state enters shell-job records.
- [x] 6.3 Cancel children during coordinated drain with bounded finalization; verify parent snapshot retains terminal and prompt disposition.
- [x] 6.4 Recover accepted children as explicit `Lost` without relaunch; verify committed cancellation remains cancelled and stale success cannot win.
- [x] 6.5 Crash at acceptance, terminal persistence, enrichment, delivery preparation, and admission; verify no duplicate child, committed result loss, or framework tool execution during recovery.
- [x] 6.6 Load the captured pre-change fixture. Verify retained history, parent prompts, and an explicit empty child ledger. Capture a baseline SQLite storage binding and marker through the real resolver. Verify the candidate preserves that row and marker. The journal fixture alone supplies no storage-binding proof.
- [x] 6.7 Probe prior-reader compatibility against new events; verify rollback instructions require backup restoration when compatibility fails.

## 7. Integrated health, evals, and sensitive fault controls

- [x] 7.1 Extend the existing background relay with explicit parent/child request IDs and barriers; verify setup/sidecar calls cannot consume child script stages.
- [ ] 7.2 Add targeted Subagents and background cases for acceptance, routed starts, controls, cancellation, prompts, and recovery; verify actual dispatch and artifact evidence.
- [x] 7.3 Preserve queued-grant revocation, shell lifecycle, and PR 1 long-task controls; verify the combined candidate passes those unchanged boundaries.
- [x] 7.4 Hold several accepted child requests. Verify independent parent dispatch and locally responsive status/cancel without provider-capacity coordination.
- [x] 7.5 Hold a child on the selected real backend that accepts concurrent requests. Verify a parent reply before child release in five critical-case trials.
- [x] 7.6 Make provider output ignore cancellation or never finish; verify framework settlement without another model response and report remote-computation limits.
- [x] 7.7 Demonstrate rejection of duplicate start, early terminal acknowledgement, late dispatch, stale approval prompt, and delivery-reset mutants in isolation.
- [x] 7.8 Keep any new focused mutation target narrow and inside the documented CI budget; verify expected mutants execute and none survives.
- [ ] 7.9 Require an independent verifier to inspect integrated results and assertions; verify no skipped cases, weakened gates, or author-only pass claims remain.

Bounded relay and response-order evidence:

- Five hosted behavioral controls pass at `8aba7cb1`. They verify canonical request attribution, sidecar isolation, setup acknowledgements, real HTTP barriers, and response bytes.
- Independent review verifies the relay assertions. The existing controls require no relay repair or duplicate test.
- Five fixed `2c043a93` observations prove parent replies before exact child release on the selected real backend.
- Actual parent and child SSE requests overlap in recorder evidence. Independent review verifies all five bindings and archived request bytes.
- Full strict results remain pass, fail, pass, pass, pass. The second trial fails the later artifact-response check.
- Recorder completion follows downstream writes. These observations prove no independent upstream EOF time or simultaneous GPU work.
- This closes tasks 7.1 and 7.5 only. Full critical-case acceptance remains open, and every original verdict stays unchanged.

Combined boundary and mutation evidence at `153d9223`:

- All three hosted platform test jobs pass. Their actual merge checkout has the exact combined source tree.
- The included controls preserve revoked queued grants, shell approval lifecycle, session-owned reap, and the prior long-task assertions.
- The long-task controls require actual file effects after 65 parent steps and 35 child steps.
- Hosted logs report assembly totals and skips. They supply no separate named pass counts for these controls.
- All fourteen mutation gates execute once. All six expected task-adoption and child-authority mutants have `Killed` status.
- The narrow gate discovers thirteen adoption tests and one child-authority test. No expected target survives, times out, or disappears.
- The four groups finish in 16m51s, 23m14s, 15m11s, and 21m01s. Each stays below the 25-minute limit.
- The `8aba7cb1` assignment group also passes in 24m52s. Its margin is eight seconds.
- Two unrelated shell-config-read mutations retain their timeout dispositions. They supply no assertion-kill claim for this gate.
- Independent review approves tasks 7.3 and 7.8. These checks prove no detector activation, days-long productivity, or full model acceptance.

Independent contract review at `a3488e95` and combined `f48e9fbe`:

- The actor runtime bytes match across both revisions. The audit finds no new runtime defect in the inspected background paths.
- The actual guard TRX records show 177 passes. The separate authority TRX records show six passes. Neither run skips a case.
- Independent review `80ceb1b3` retains the fault controls and their valid pairs. All eleven listed source and test pins match the combined candidate.
- The retained PR 2 snapshot reports 29 successful checks. Both native logs report 29 tapes and 10 scenarios passed.
- Current observation review `7742d477` proves one held-child reply, canonical result consumption, and a complete artifact read. It supplies no five-pass acceptance claim.
- The delta now distinguishes the audience operating core and deployment playbook from separately scoped project instructions. It excludes `SOUL.md` and `TOOLING.md`.
- Public children retain the existing exclusion of project-local instructions. Prompt content grants no additional authority.
- This specification repair changes no runtime, prefill behavior, prompt bytes, or test assertion. It adds no fresh model evidence for guide version `1.0.3`.
- The inherited `per-call-tool-recurrence` delta now matches the canonical prompt clauses and three routed prompt scenarios. Its recurrence and authority clauses remain unchanged.
- Task 7.9 remains open for independent review of the integrated result. Tasks 7.2, 8.6, and 8.7 also remain open.

## 8. Docs, operational guidance, and handoff

- [x] 8.1 Rename the glossary entry to approval prompt with a legacy-anchor alias; verify old links still resolve and exact code identifiers remain unchanged.
- [x] 8.2 Update `SPEC-002`, `SPEC-016`, and the authorization architecture document; verify they describe implemented ownership and cancellation order.
- [x] 8.3 Add minimum runtime `AGENTS.md` guidance; verify it describes background acceptance, deferred control, partial result review, and cancel/recreate only.
- [x] 8.4 Update `subagent-authoring` and `netclaw-operations` versions; verify instructions advertise no deferred messages or restored tool ceilings.
- [x] 8.5 Run targeted identity/tool/skill evals for those changed instructions; verify selected resource and control receipts satisfy the actual task.
- [ ] 8.6 Run applicable builds, required tests, Slopwatch, and copyright-header verification; retain exact candidate and meaningful test counts.
- [ ] 8.7 Validate the OpenSpec change strictly and sync implemented deltas through the appropriate skill; verify the PRD and active plan trace to the final contract.
- [x] 8.8 Prepare rollback and evidence handoff for the combined PR; verify no merge, rollout, publication, or database replacement is implied by local passes.

Rollback instructions and evidence handoff at `a3488e95`:

- The [runbook](../../../docs/runbooks/subagents.md#background-child-upgrade-and-rollback) requires stopped ingress, coordinated drain, and a recorded pre-upgrade backup.
- The [canonical plan](../../../.systematize/plans/background-subagents/plan.html#q-rollout) retains separate activation and rollback authority.
- The retained prior reader accepts its `sid-v1` control and rejects `cra-v1` and `cre-v1`. Direct binary downgrade has no compatibility proof.
- Rollback therefore requires the recorded pre-upgrade backup. Reader and restoration receipts prove only their stated storage scope.
- The evidence notes retain exact source revisions, local test counts, hosted checks, original failed observations, and open model gates.
- This closes task 8.8 for instructions and handoff preparation only. No live rollback, merge, rollout, publication, or database replacement receives authority.

Documentation scope review at `2357991d`:

- `SPEC-002`, `SPEC-016`, and the authorization architecture describe the implemented owner, original requester, prompt lifetime, and cancellation order.
- `PrepareForDaemonRestart` admits child cancellation and waits for bounded framework finalization before the drain acknowledgement.
- An abrupt actor stop closes live dispatch and requests token cancellation without a durable finalization guarantee.
- Recovery retains committed terminal or cancellation facts. An unresolved accepted run becomes `Lost`.
- Independent source review approves the two-document clarification. It changes no runtime or detector policy.
- This closes task 8.2. The remaining integrated health, model, specification-sync, and handoff gates stay open.

Targeted instruction eval evidence at `8a57fbf5`:

- The eight changed instruction files match the prior discovery and cancellation observations.
- Five discovery observations load the canonical coordination skill and the complete selected resource.
- Their original results remain pass, pass, fail, pass, pass. A separate corrected-oracle assessment accepts all five archives.
- Five cancellation observations pass with unchanged instruction bytes. They prove deferred controls, terminal attribution, complete partial-report reads, and final status closure.
- The operations case reads the complete selected child-run resource. Its activation preview proves no complete operations-guide read.
- One fresh author-guide observation passes after the narrow assertion repair. It loads the complete version `1.5.0` guide and returns the required contract answer.
- The original author-guide failure remains false. All 54 files remain unchanged; the separate offline assessment adds no replacement verdict.
- Independent review verifies actual provider pairs, database verdicts, source equivalence, fresh identities, and cleanup.
- These observations exercise the private identity. Public identity and CLI templates retain evidence of equal source.
- This closes task 8.5 only. The explanation proves no executed restart, late approval, framework finalization restriction, or task that lasts days.
- Integrated health, full critical-case acceptance, specification sync, and final handoff remain open.
