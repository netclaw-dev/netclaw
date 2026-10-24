## 1. Establish the test contract

- [x] 1.1 Record the prior frozen-suite evidence: 51 tests, 15 red failures, then 51 green passes; exclude adversarial acceptance.
- [x] 1.2 Verify all three files match `2661-tests.sha256`; retain their existing assertions.
- [x] 1.3 Add separate adversarial test files with pure decision tables and one orchestration theory in the existing actor fixture.
- [x] 1.4 Parent: prove the new findings fail against the current implementation; inspect each failure before a different implementer changes production code.
- [x] 1.5 Parent: prove the engine-batch row red through the selected internal `EvaluateAndApplyAsync` seam; require an exception and unchanged storage.

## 2. Correct the shared decision path

- [x] 2.1 Record the prior implementer stage under `openspec-apply-change`; preserve its frozen tests for the next implementation stage.
- [x] 2.2 Implement the normalized same-name check before high-overlap consolidation; leave runtime proof to task 3.1.
- [x] 2.3 Validate every `UPDATE` and `CONSOLIDATE` target against exact candidate identities before acceptance and effects; leave runtime proof to task 3.1.
- [x] 2.4 Reject malformed keyword prefixes without prompt changes; leave parser and evaluator runtime proof to task 3.1.
- [x] 2.5 Preflight both consumer batches before persistent effects; reuse the actor list and internal engine seam; leave runtime proof to task 3.1.
- [x] 2.6 Parent: prove valid-target append fallback and nominee controls; pass the shared pre/post state assertions.
- [x] 2.7 Parent: prove real-engine failure and worker retry exhaustion with one explicit-request checkpoint; require retry count 5, failed status, unchanged memory, and no completion.

## 3. Verify and hand off

- [x] 3.1 Parent: execute the filtered project under the shared lock after the disk check; inspect results and preserve unrelated assertions.
- [x] 3.2 Replace the throw-span mutation proposal with the same-name condition-only plan; retain paired xUnit 2-compatible pure tests.
- [x] 3.3 Parent: select the mutation gate after red proof; measure exact nonzero mutants, span membership, killed status, and cold-run cost.
- [x] 3.4 Run Slopwatch, the header check, and strict OpenSpec validation on the final correction; verify no new violations or artifact errors.
- [x] 3.5 Document extra LLM cost, failure behavior, target validation, both consumer loops, and the selected engine batch test seam.
- [x] 3.6 Parent: require independent review and green CI; sync and archive through OpenSpec skills only after acceptance.

Source PRDs: [PRD-007](../../../../docs/prd/PRD-007-agent-personality-and-local-memory.md), product outcome 2.
Capability: [netclaw-agent-memory delta](specs/netclaw-agent-memory/spec.md).

The first frozen suite records 15 failures, then 51 passes.
The corrected adversarial suite records 15 behavioral failures and 15 control passes before the separate implementation.
Both batch rows detect anchor changes on that baseline.
The final filtered suite records 111 passes, no failures, and no skips.
It includes valid-target append, normalized names, nominee controls, actor recovery, engine preflight, and worker retry exhaustion.
All six frozen test files retain their hashes.
The separate Astra xhigh review finds no additional production defect.
The engine batch test calls the existing internal seam; it does not prove public multi-proposal extraction.
Preflight does not promise rollback after a storage failure.
The measured condition has one killed equality mutant; eight unsafe report controls fail.
Final gate execution, hosted group duration, hosted CI, and the separate post-open review remain acceptance gates.
The parent retains raw logs and TRX outside the repository.

The final culture regression has two failing baseline rows and 113 final passes.
The separate post-open reviews find no further source defect at `81edeee7c61ec6bf150a91de3c14c58645e19400`.
All hosted checks for that code head pass or skip as configured.
The complete mutation group passes in 659 seconds, below the 900-second budget.
The main specification matches the delta.
The rebase retains identical curation production files, tests, mutation code, and specification content.
The final rebased and archived head must pass CI before merge.
