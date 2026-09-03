## 1. Baseline And Standalone Proof

- [ ] 1.1 Recheck active OpenSpec changes that touch tools, subagents, or deferred exposure; verify this change replaces no unarchived requirement by accident.
- [ ] 1.2 Create `CycleDetectorLab` in an approved temporary directory with only `Program.cs` and `cases.jsonl`; verify it builds with the .NET base class library only.
- [ ] 1.3 Add all 15 sanitized matrix cases from design section 7; verify each expected decision passes and the process exits nonzero on a mismatch.
- [ ] 1.4 Add fixed-seed property checks for at least 10,000 sequences; verify canonicalization, reset, result-change, and six-entry-bound properties pass twice with equal output.
- [ ] 1.5 Run a private extractor against the known incident and representative long successful sessions; verify the third repeated action blocks and no confirmed false execution block exists.
- [ ] 1.6 Inspect the extractor output for private data; verify it contains aggregate counts and ordinals only, then keep all laboratory and replay files untracked.
- [ ] 1.7 Run both synthetic direct-model probe paths for multiple trials; verify the model changes action or reaches a truthful text-only stop.

## 2. Adjacent Contract Repairs

- [ ] 2.1 Preserve loaded schemas after successful normal compaction; verify an actor test loads a deferred tool, compacts, and exposes it on the resumed call.
- [ ] 2.2 Evict loaded schemas before context-overflow recovery compaction; verify the retried call exposes only the policy-exposed core.
- [ ] 2.3 Classify typed MCP `isError: true` results as `TransientFailure` in both invocation paths; verify `isError: false` text that starts with `Error:` stays successful.
- [ ] 2.4 Make text-only state monotonic for the current turn; verify empty responses and normal compaction cannot restore tools.

## 3. Observe-Only Detector

- [ ] 3.1 Add immutable signature values and a pure canonical JSON hash factory; verify object-order, array-order, metadata, identifier, call-ID, and duplicate-member tests.
- [ ] 3.2 Add the bounded period-one through period-three algorithm to `TurnStateTracker`; verify positive cycles and all specified counterexamples with table-driven tests.
- [ ] 3.3 Map each parallel result to its request before observation; verify a mixed-progress batch differs and the history never exceeds six entries.
- [ ] 3.4 Wire candidate and completion observation into the parent actor without blocking; verify normal execution remains unchanged and diagnostics report `would_block`.
- [ ] 3.5 Canonicalize child tool names and wire the same tracker into the child actor; verify parent and child decisions match for an equal replay corpus.
- [ ] 3.6 Verify cancellation, partial batch failure, and approval redrive do not record duplicate completed iterations or execute a batch twice.
- [ ] 3.7 Inspect logs and traces from all detector tests; verify no argument, result, hash, session, user, channel, path, or cursor value appears.
- [ ] 3.8 Collect observe-only runtime evidence and compare it with the laboratory report; verify every proposed block has a manual disposition and no confirmed false block exists.

## 4. Synthetic Correction

- [ ] 4.1 Add the closed `BreakToolCycle` remediation code and one bounded correction message; verify its receipt has no successful activity or authority effect.
- [ ] 4.2 Enable the parent first-block path through normal batch-start, single-result, and batch-completion handlers; verify the journal contains one result per call and no tool runs.
- [ ] 4.3 Enable the child first-block path through its normal result history; verify each call has one result and another allowed action remains available.
- [ ] 4.4 Keep synthetic corrections outside completed-cycle history; verify a correction does not create a new false period.
- [ ] 4.5 Prove a repeated mutation batch stops before its third side effect; verify the authorized tool fake records exactly two executions.

## 5. Terminal Stop

- [ ] 5.1 Add the second-intervention decision for the last blocked action; verify a different completed action clears that blocked-action state.
- [ ] 5.2 Enable parent text-only completion without persisting an orphaned second tool-use message; verify strict provider history validation passes.
- [ ] 5.3 Enable child text-only completion and a partial outcome reason; verify the child reports completed work without a blocked-operation success claim.
- [ ] 5.4 Prove parent and child text-only state survives empty-response retries; verify no retry exposes a tool definition.
- [ ] 5.5 Prove compaction preserves detector and text-only state; verify a cycle that spans compaction blocks at the expected candidate.

## 6. Remove Temporary Iteration Limits

- [ ] 6.1 Record the final replay, shadow, correction, and terminal-stop evidence; verify every pre-integration and rollout acceptance gate passes before limit removal starts.
- [ ] 6.2 Remove `Session.MaxToolIterationsPerTurn` from configuration, schema, environment examples, doctor tests, and configuration documentation; verify correction removes only that deprecated property.
- [ ] 6.3 Remove the child iteration constant and related budget branches; verify a productive parent and child can exceed the former limits.
- [ ] 6.4 Retain iteration counts for aggregate telemetry; verify count alone never causes a stop after limit removal.
- [ ] 6.5 Update `netclaw-operations` guidance and bump its skill version; verify the guidance explains cycle diagnostics and the removed configuration property.
- [ ] 6.6 Run `./evals/run-evals.sh`; verify all cases pass after the `SessionConfig` default change.

## 7. Quality And Closure

- [ ] 7.1 Run the focused actor, MCP, configuration, doctor, compaction, approval, and subagent tests sequentially; verify every project passes without a shared-output lock.
- [ ] 7.2 Run `dotnet restore Netclaw.slnx`, the solution build, and the full test suite; verify each command passes from the clean worktree.
- [ ] 7.3 Run OpenCover CRAP analysis for changed complex methods; verify each new detector method has a CRAP score below 30 and no high-risk untested branch remains.
- [ ] 7.4 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`; verify both commands pass with no new baseline entry.
- [ ] 7.5 Run `openspec validate stop-repeated-tool-cycles --type change --strict` and `/opsx-verify`; verify implementation, specifications, and tasks agree.
- [ ] 7.6 Inspect the complete diff and generated artifacts; verify no private transcript, raw payload, hash, operational endpoint, or personal identifier is tracked.
- [ ] 7.7 Sync and archive the change only after all tasks pass; verify the main specifications contain the final cycle contract and no static limit requirement.
