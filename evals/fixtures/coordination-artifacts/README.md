# Coordination Artifact Oracle Fixtures

This fixture supports `coordinate-background-agent-work` tasks 2.3, 2.7, 5.6, 5.9, 5.10, and 6.2.
It adds oracle controls. It closes no task and proves no actual workflow.

## Independent Source Truth

`source/catalog.py` contains a small catalog with two known facts.
`Catalog.refresh` publishes a candidate before duplicate validation.
`Catalog.read` returns the stored record order.
The plan must put validation before publication and preserve valid record order.
The user still owns the decision about identifier case rules.
These facts describe this fixture. They identify no Netclaw production defect.

`truth.json` fixes the source hashes, evidence ranges, owners, references, checks, risks, and open decision.
The oracle recomputes each source hash from actual bytes.
It computes the revision from sorted relative paths and their hashes:

```text
revision = "sha256:" + sha256(concat(path + NUL + file_sha256 + LF))
```

This identity is not a Git commit or a runtime execution receipt.
The oracle must receive a trusted fixture root from the eval operator.
The model's artifact cannot supply or replace that root or its truth file.
Future runtime cases must also verify the source bytes that the children receive.

## Artifact Contract

The complete files follow the current `agent-coordination` findings and plan templates.
The parser accepts changes to heading case and whitespace.
The parser does not require exact prose, a minimum length, or a general prohibition on angle brackets.
It rejects the templates' known unresolved placeholders in required content.
It supports simple Markdown tables. It does not support escaped table separators.

The findings require the current source revision and all template sections and fields.
Each E row cites its fixed source range, revision, and exact source excerpts.
Each F row identifies its owner, valid E references, and status.
The plan requires the current revision, valid F references, concrete A actions, and C acceptance checks.
The risk table links R1 to both C1 and C2.
The findings and plan retain Q1 and its decision owner.
Each required table cell and field must contain a value.

The source-specific identifier checks are bounded checks for this task.
They do not prove action order, establish general prose comprehension, or reject every possible misleading statement.
Actual model artifacts still need independent review.

The complete findings and plan form the valid pair.
Separate defective files contain a stale revision, a forged evidence or finding ID, a placeholder, or a summary.
The independent test suite supplies further mutations and runtime-shaped counterexamples.

## Public Python Contract

`evals/coordination_artifact_evals.py` exports:

```python
verify_artifacts(fixture_root, findings_path, plan_path)
verify(contract, observer_receipt, events, eval_home, fixture_root)
```

Each function returns this report:

```json
{"passed": false, "checks": {}, "errors": ["missing-proof: ..."], "limits": []}
```

The error prefixes distinguish `artifact:`, `evidence:`, `missing-proof:`, and `delivery:` failures.
An invalid low-level input can also return the underlying input error.
The caller must use `passed`, not the presence of a successful partial check.

The runtime contract contains these case-owned values:

```json
{
  "session_id": "the actual owner session",
  "prompt_nonce": "the fresh case nonce",
  "findings_path": "/home/netclaw/.netclaw/sessions/case/findings.md",
  "plan_path": "/home/netclaw/.netclaw/sessions/case/plan.md",
  "delivery": "delivered"
}
```

`delivery` accepts `delivered` or `blocked`.
The existing observer receipt must match `session_id` and `prompt_nonce`.
The oracle resolves runtime paths through `child_run_evals.actual_file`.
The eval mount resides at `eval_home/data`.
The resolver rejects path escapes and links outside that mount.

## Actual Runtime Producer

`SessionObserver` writes `session-output.jsonl` through its existing `ReadThroughAsync` loop.
Each record contains `sequence`, `observed_ns`, and `output`.
`output` contains the actual PascalCase `SessionOutputDto` fields from `SessionOutputDtoMapper`.
The oracle adds no runtime field and changes no observer mode.

The consumer requires consecutive record sequences and ordered observer times.
Every DTO must belong to the case session.
The consumer pairs `tool_call` and `tool_result` occurrences by `CallId` and `ToolName`.
A completed call ID can recur. An unresolved call ID cannot overlap another occurrence.
Arguments use the actual tool schema, including `Agent`, `Task`, and `Context` for `spawn_agent`.

The parent must read the full current findings before it calls the plan worker.
The matched plan assignment includes both artifact paths in its `Task` or `Context`.
Its result must contain an accepted or current running run ID.
This narrow check does not replace the existing lifecycle ownership and terminal attribution gates.

The parent must read the full current plan before `attach_file`.
Each successful direct-read DTO result must equal the complete actual artifact text.
An actual read receipt proves access. It does not prove comprehension.
The fixed artifact checks provide separate, bounded content evidence.

The tiny neutral artifacts fit the ordinary inline result size.
The oracle reports missing proof when a valid read uses pagination, spill, or a configured smaller inline limit.
Those read behaviors remain permitted and are not unsafe.
Later consumer integration must add exact composition proof before broad workflow acceptance.
Actor diagnostic logs can truncate result previews to 200 characters, so this oracle uses raw DTO results.

## Actual File Output

The parent must call `attach_file` with the reviewed plan path.
The successful paired result must match the current `File attached:` receipt format.
The later same-session `file` DTO must identify the same path, name, and MIME type.
The emitted file bytes must equal the reviewed plan bytes.
An actual attachment copy remains permitted when its receipt, DTO, and bytes agree.
The actor emits the tool result before its File output.

The existing direct attachment case defines this receipt contract in `evals/run-evals.sh`.
The File DTO has no call ID.
This bounded case requires one plan attach attempt and exactly one matching File output.
Later broader cases must retain an unambiguous occurrence association.

A blocked case requires an actual-shaped denied attach receipt or the exact missing-file result.
The denied form uses `ToolFailureCode: "access_denied"` with a nonempty result.
The missing-file form uses `Error: File not found: <plan path>` and can have no failure code.
The blocked case must contain no File output.
Its pass proves only the failed action and absent File output.
It never closes the independent model claim review gate.
The delivered case fails when the attachment is blocked, regardless of the final response.

This contract proves only the headless file output surface.
It proves no remote chat upload or user receipt.
A final response, a child success flag, or a file path alone cannot replace the required receipts.

## Command Contract

```bash
python3 evals/coordination_artifact_evals.py \
  --fixture-root evals/fixtures/coordination-artifacts \
  --eval-home /path/to/eval-home \
  --contract /path/to/case-contract.json \
  --receipt /path/to/observer-receipt.json \
  --events /path/to/session-output.jsonl
```

The command writes the JSON report to stdout.
It returns zero only when `passed` is true.
The event file is the existing observer's raw JSONL output.
This slice registers no new eval case and changes no harness gate.

## Proof Limits

Synthetic controls test oracle sensitivity. They do not become actual runtime evidence.
Actual scripted runtime proof must drive the real tools and preserve raw DTOs and files.
The existing `collect` mode supports dynamic accepted runs and archives File DTOs before the final parent turn boundary.
Later case integration can reuse that mode and require this oracle to accept the completed attempt.
No new observer mode follows from this slice.
Actual model trials must retain independent artifact and user-claim review.
All related OpenSpec task checkboxes remain open until their full proof exists.
