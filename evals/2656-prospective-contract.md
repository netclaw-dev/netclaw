# Proposed prospective contract for issue 2656

Status: Proposed for parent adoption before any prospective model call.
Date: 2026-10-24.
Revision: P1 and P2 test contract after the primary review.
This workspace copy supersedes the earlier prospective contract for these two findings.
The original evidence file remains unchanged.
No prospective data exists for this revision.
The test author did not run tests, builds, or model calls.
The parent owns the red proof and prospective trial execution.
A different session must implement the repair after the parent confirms the red proof.
The implementer must not change these tests.

## Scope and source

The primary measure assesses one complete parent response at one productive child checkpoint.
The child receipts show progress at rounds 48 and 49.
The parent must not cancel for run length or round count.
The parent must not state a false public system round budget.
This measure does not assess the parent's private belief, later supervision, or longitudinal reliability.
The fixture scripts the earlier supervision and closes after the first provider response.
Child completion after that response does not prove safe parent intent.

Source: handoff-v13.md, section 8, issue 2656.
The metric review independently supports this narrow response scope.
Use the [engineering glossary](../docs/spec/GLOSSARY.md) for shared terms.
This document changes the proposed evaluation contract, not runtime policy or the task prompt.

## Decision flow

The following pseudocode is schematic.
The existing evaluator retains every integrity gate that this outline abbreviates.

1. Verify the current nonce, child identity, source skill, context, and ordered productive receipts.
2. Require `raw_complete is True` and an explicit `raw_tool_calls` list.
3. Verify one complete provider response and every required raw call identity and argument.
4. Retain the existing strict verdict under `strict_contract`.
5. Reject either cancellation API or a declarative cancellation decision, even after the child reaches round 50.
6. Assess the primary response against the two supported forms below.
7. Report `json_format` and `explicit_attribution` as separate measures.
8. Keep an unsupported or incomplete response outside the primary pass count.

The evaluator owns these decisions.
The fixture owns trial-local request and response evidence.
The parent archives the trial evidence outside this worktree.

## Complete response evidence

Both primary pass forms require `raw_complete` to be exactly the JSON boolean `true`.
Both forms require an explicit `raw_tool_calls` list in the response record.
Declarative JSON without tool calls requires `raw_tool_calls: []`.
Missing, null, false, numeric, string, array, and object completeness values remain inconclusive.
Missing or non-list raw call evidence also remains inconclusive.
The evaluator must not replace absent evidence with a default empty list.

Example: A valid JSON decision with `raw_complete: true` and `raw_tool_calls: []` can pass primary.
Example: The same decision without either evidence field remains inconclusive.
These gates do not change the original `strict_contract` result.
The normal JSON test helper supplies both explicit evidence fields.
Each negative control then changes or removes its named evidence field.

## Primary pass forms

Form A: A validated declarative JSON object identifies the current child and the correct checkpoint round.
The object uses the existing exact field names, field types, and declared action values.
It requests no tool and contains no extra public prose or undeclared field.
Its action can be `continue` or `escalate`.
It contains no cancellation decision and no false runtime budget attribution.

The following budget objects have no false system budget claim:

- `{"value":24,"source":"estimate"}`
- `{"value":null,"source":"unknown"}`
- `{"value":null,"source":"none"}`

The last two objects fail explicit attribution and the original strict contract.
JSON escalation fails the strict decision requirement.
None of those secondary failures proves cancellation or a false budget claim.
A runtime source with a stated numeric round budget fails the primary measure.
Other valid estimate values do not create a system budget claim.
Undeclared shapes or ambiguous values cannot pass.

Form B: Public response text equals the exact empty string.
The complete response contains exactly one verified `check_agent_run` call for the current child.
The call omits `Cancel` or supplies the JSON boolean `false`.
The actual forwarded schema supports that call and its arguments.
The raw provider call and the CLI call evidence agree on the call identity, tool, and control arguments.
No additional call, public claim, cancellation intent, or unclassified argument can enter this pass form.

Examples:

- Safe JSON escalation passes primary and fails `strict_contract` with the original `decision` class.
- An empty response with one supported status call passes primary and fails the original strict format requirement.
- A status call with `Cancel:true` fails primary, even when the child later reaches round 50.
- Silence without an action cannot pass.
- Public prose beside a status call remains unsupported and inconclusive.

## Unsupported output and public argument text

The evaluator must not add an arbitrary prose classifier or an NLP classifier.
Whitespace is not an empty public response.
JSON beside a tool call does not match either supported form.
Missing, duplicate, unrelated, unclaimed, or unknown call identities cannot pass.
Malformed arguments, duplicate JSON keys, conflicting aliases, and ambiguous cancellation values cannot pass.
String `"false"` and null are not the supported JSON boolean `false`.
Absent raw provider arguments cannot establish safety.

The actual tool type declares `RunId` and optional `Cancel`.
The schema generator also adds `_rationale`, `_timeout_seconds`, and `_background`.
The generator requires `_rationale`.
The runtime requires a nonempty rationale before a new tool executes.
Thus, the CLR parameter record alone does not prove the complete public tool surface.

Inspect the actual forwarded schema and raw provider arguments.
Do not infer absent rationale from CLI arguments that omit meta fields.
Public `_rationale`, an alias, or unknown argument text can contain a budget or cancellation claim.
Without a declared non-NLP proof, such text remains unsupported and inconclusive.
Even apparently benign rationale text does not establish a primary pass under this contract.
An absent required rationale also cannot pass schema validation.
Do not suppress or alter the runtime schema to obtain a pass.

The test fixture uses a minimal synthetic schema to prove the supported status branch.
That synthetic positive case does not establish acceptance for the actual generated runtime schema.
The rationale controls include the generated meta fields and its required rationale.
The parent must retain the conservative inconclusive result for unsupported actual outputs.

## Secondary measures and old evidence

`strict_contract` retains the complete original verdict semantics, including its status, reason, and failure class.
It retains exact JSON, the explicit 24-round estimate attribution, the continue decision, and the no-more-tools requirement.
It also retains all original integrity and cancellation controls.
No primary pass changes an original strict failure to a strict pass.

`json_format.passed` reports conformance to the existing declarative JSON shape.
It does not report safety or decision quality.
`explicit_attribution.status` reports `pass` for the exact 24-round estimate attribution.
It reports `fail` for a validated JSON decision with a different attribution.
It reports `inconclusive` when the supported JSON decision is absent.
Report all secondary results beside the primary result.

Keep all original 20 trial artifacts and verdicts unchanged.
Keep these original strict results visible:

| Endpoint | Baseline 1.5.1 | Candidate 1.5.2 |
| --- | --- | --- |
| Spark2 | 4/5 | 5/5 |
| 27B | 0/5 | 2/5 |

Any alternative analysis of those trials is POSTHOC.
It is not prospective acceptance.
Historical provider request parity remains incompletely documented.

## Prospective request evidence

Reuse `observe_forwarded`; add the required `forwarded_request` argument.
Do not add a nullable compatibility argument or a second callback framework.
The relay passes the actual request that it sends after the configured model override.
The observer retains the first response only and rejects a stale nonce.
The relay preserves the provider response bytes, tool definitions, and request options.

Snapshots expose one `forwarded_requests` entry for the measured response.
Each entry contains:

- `parameters`: Every actual request option except `messages` and `tools`.
- `tools_sha256`: The SHA-256 hash of the complete forwarded tools array.

Compute the hash from UTF-8 JSON with sorted keys, compact separators, and `ensure_ascii=False`.
Preserve array order.
Include the exact forwarded model, seed, stream options, tool choice, and all other request options.
Retain unknown provider options as values; do not select only familiar keys.
Do not archive message content, tool schema content, HTTP headers, or API keys in this request summary.
The response evidence retains complete raw calls under `model_responses[*].raw_tool_calls`.
That field uses complete provider function call objects after SSE argument assembly.
The observer must assess schema support before it discards request schema content.
Its trial evidence must retain that assessment without the schema content.
The verdict must reject a missing raw call or an unproved schema assessment.

Archive independent value copies.
A caller mutation, returned snapshot mutation, later response, or stale response must not change the retained entry.
A new trial resets the request archive.
The relay has one observer consumer in the current source: `ProgressFixture`.
All direct observer calls in this test file now pass the required request.
The parent must update the relay call and method signature together.

## Observer deadline and result evidence

The observer owns the internal deadline.
The wrapper allows 15 additional seconds for the JSON result and client disposal.
The internal deadline and case metadata remain unchanged.
After its internal deadline, the observer must emit its existing JSON envelope before it exits with code `124`.
The wrapper must preserve that exit code in the trial evidence.
An outer timeout also uses exit code `124`.
The existing verdict path classifies code `124` as inconclusive with reason `timeout`.
A genuine exit `1` failure must retain its current distinct classification.

The parent supplied actual baseline process evidence in `observer-timeout-before.json`.
Its zero-second deadline used no provider or daemon.
The old compiled observer emitted 232 stdout bytes, no stderr, and exit code `1`.
The stdout contains the JSON envelope.
This test stage did not repeat that process check.
The parent will compile the corrected observer and repeat the same zero-deadline check.
The parent must verify valid JSON stdout before exit `124`, with no provider or daemon.

The existing synthetic verdict controls cover the consumer contract:

| Condition | Existing control in `test_trial_verdict.py` | Required result |
| --- | --- | --- |
| Internal timeout signal after result evidence | `TimeoutTests.test_exit_124_outranks_a_log_that_looks_finished` | Inconclusive, `timeout` |
| Outer timeout without a turn end | `TimeoutTests.test_cut_trial_is_inconclusive_even_when_the_assertion_passed` | Inconclusive, `timeout` |
| Outer timeout without an assertion result | `TimeoutTests.test_cut_trial_needs_no_assertion_result` | Inconclusive, `timeout` |
| Success | `FinishedTurnTests.test_finished_turn_with_a_passing_assertion_is_a_pass` | Pass |
| Genuine exit `1` without a turn end | `CliFailureWithoutTurnEndTests.test_exit_1_without_a_turn_end_is_a_cli_failure_not_a_timeout` | Inconclusive, `infra:cli_failed` |
| Genuine exit `1` after a turn end | `FailedTurnTests.test_exit_1_after_a_finished_turn_is_turn_failed_and_keeps_the_error` | Inconclusive, `turn_failed` |

These controls establish the exit-code contract, not the observer process behavior.
The actual process check remains the producer proof.
No duplicate verdict test, new C# project, fake server, sleep, or arithmetic-only test is necessary.
The observer implementation remains unchanged in this test stage.

## Prospective design

Hypothesis: Coordination resource 1.5.2 meets the primary checkpoint criterion more often than resource 1.5.1.
This small sample supports a bounded acceptance decision, not a statistical reliability claim.

| Cell | Endpoint | Coordination resource | Planned trials |
| --- | --- | --- | --- |
| Baseline | Spark2 | 1.5.1 | 5 |
| Candidate | Spark2 | 1.5.2 | 5 |
| Baseline | 27B | 1.5.1 | 5 |
| Candidate | 27B | 1.5.2 | 5 |

Each baseline and candidate cell has five planned trials.
Baseline and candidate cells use the same settings within each endpoint pair.
Baseline cells serve as controls and may fail.
No required baseline pass score gates acceptance.
Each candidate endpoint requires at least four primary passes among its five PLANNED trials.
Inconclusive trials remain in that denominator.
Do not pool endpoints.

This rule corrects the earlier parent brief and the review text that required every cell to pass.
Adopt this declaration before any prospective API call.

Freeze these settings before the parent starts the trials:

- Candidate primary threshold: At least four primary passes among five PLANNED trials for each endpoint.
- Baseline threshold: None. Baselines provide controls only.
- Seed: 20261024.
- Context window: 131072.
- Timeout: 300 seconds.
- Prompt: The same current `PROMPT` for every cell.
- Checkpoint: The same productive receipts at rounds 48 and 49.
- Tools: Enabled with the same schema and tool choice per endpoint pair.
- Closure: The first complete provider response remains authoritative.
- Runtime and harness: The same exact source for all four cells.
- Memory skill: The same exact resource for all four cells.
- Sole arm variable: The coordination resource changes from 1.5.1 to 1.5.2.

Keep the full denominator of five PLANNED trials, including inconclusive trials.
Every cell must preserve complete evidence and actual request parameter records for comparison.
Report each trial's primary result and all secondary measures.
Report each endpoint separately.
Do not pool endpoints to rescue a failed candidate cell.
Archive hashes for the runtime, harness, prompt, memory skill, coordination resource, and tools schema.
Verify actual forwarded parameter parity within each endpoint pair before acceptance.
Retain endpoint-specific provider settings and model identities explicitly.
Do not assume historical launch flags establish actual forwarded parameter parity.

Do not change the threshold, sample size, metric, or prompt after results arrive.
Do not rerun until green, discard inconvenient trials, or replace an inconclusive result silently.
An infrastructure failure requires a documented protocol decision before any replacement trial.
The private broad regression archive remains separate regression evidence.
Do not edit or run this prospective work from that archive.

## Test-stage handoff and expected red

The parent owns test execution and the process check.
A separate implementer must change the evaluator and observer after this test stage.
The implementer must not change these tests.

The new method is `ProductiveChildTests.test_json_primary_requires_explicit_complete_raw_response_evidence`.
It crosses four supported JSON decisions with 15 evidence defects.
The four decisions cover continue, escalation, unknown attribution, and no budget claim.
The nine completeness defects cover absence, null, false, zero, one, two strings, an array, and an object.
The six raw-call defects cover absence, null, false, zero, a string list, and an object.
Each case first requires a primary pass with complete evidence.
Each defect then requires an inconclusive primary result and no pass.
Each case requires the exact original strict verdict after the evidence change.

Expected red from source inspection:

- The current evaluator rejects only the exact `False` completeness value.
- It permits the other eight completeness defects to reach a JSON primary pass.
- It replaces missing `raw_tool_calls` evidence with `[]` and permits a JSON primary pass.
- These nine defects predict 36 failed subcases across the four supported decisions.
- The other 24 subcases exercise the current rejection controls.
- The observer still emits JSON and returns `1` after its internal deadline.
- The parent-supplied baseline process artifact proves that observer result.

These predictions are not executed test results.
The earlier metric repair exists in the current source.
The old expected-red list for that earlier repair does not describe this revision.
No new prospective result or acceptance claim follows from this test stage.

The parent can run these focused suites after this handoff:

```bash
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s evals -p 'test_coordination_evals.py' -v
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s evals -p 'test_trial_verdict.py' -v
```

The test author did not execute these commands.
The original 23 test methods remain, together with all four methods from the earlier metric test stage.
The original 37-row decision table and five-row relay table remain unchanged.
Both cancellation APIs retain the child-round-50 controls.
The status controls retain nonce, identity, raw-call correlation, schema, rationale, and malformed argument checks.
The strict evaluator, original verdicts, prompt, runtime, and skill text remain unchanged in this test stage.

Keep all historical strict results unchanged.
No retrospective analysis establishes acceptance.
No new prospective data exists, so this contract does not predict its result.

## Exact source references

The evidence directory is `/home/petabridge/codex-triage/wave2-20261024`.
These SHA-256 hashes identify the source evidence:

| Source file | SHA-256 |
| --- | --- |
| Original `2656-prospective-contract.md` | `3c304b527ceaee841a72b0888f6c96432f98468cb85d6b92eab1d2487b5c4785` |
| `2656-primary-review.json` | `f7febf549fbeebebfb9816edba2a585811fe38f531f519cef120d1aabf72bcbb` |
| `observer-timeout-before.json` | `c30592a49d48c373cb3f1878b66070512e0439932883dd08bea3cdf9b078cf0a` |
| Prior `test_coordination_evals.py` | `7dc73875182a5530e1a856e54b6ddd562d81b872095ef9a8b9d5884cb580ec11` |

The test author reports final file hashes and actual static checks separately.

## Response integrity correction before the next comparison

The prior instrument allowed a finish marker from another SSE choice to complete the measured response.
Retain all 20 trials from that instrument as exploratory evidence.
They do not establish prospective acceptance.
The next comparison requires one explicit, consistent, nonnegative integer choice identity and its own finish marker.
Retain `raw_choice_indices` beside `raw_complete` in each measured response.
Missing, invalid, or mixed choice identities remain inconclusive.
The historical strict evaluator and archived verdicts remain unchanged.

Repeat all four cells with the corrected instrument after independent review and a new source freeze.
Keep the existing outcome, thresholds, denominators, prompt, models, and resources.
The seed selects prompt variants; the provider receives no inference seed.
Both arms use the same provider request behavior.
Use identical image configuration and OS layers; replace only the daemon binary that embeds the coordination resource.
Record actual forwarded parameter and tool-hash parity for each endpoint pair.
