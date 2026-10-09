# Persistent child-run eval consumer

The helper uses one real `DaemonClient` for each trial.
It records actual `SessionOutputDto` values through the public mapper.
It does not change the product CLI or attach another session connection.

## Fixed flow

1. Create a fresh headless session and submit one child task.
2. Read the paired canonical acceptance before a child ownership assertion.
3. Observe the initial parent reply.
4. Wait for the first actual upstream child response payload, then submit one independent parent probe.
5. Read an authorized status result and the actual child log.
6. Bind the held provider request to the accepted identifiers and returned paths.
7. Require the probe reply before child release.
8. Release the held upstream response payload.
9. Observe actual terminal consumption and the parent response without another user input.
10. Verify commit logs, actual provider history, and actual artifact bytes.

The partial-cancel case holds an upstream response after a paired artifact-write result.
The parent probe reads the live log before it requests cancellation.
The harness then releases the old response payload and checks the partial evidence.
The oracle rejects any later child provider admission.
The cancel result can share the second parent probe response.
A fast result can share the initial response in a legacy case.
Compatible sibling results can share one continuation.
The consumer requires actual terminal call/result pairs and later response bytes in parent provider requests.
It does not assign one turn to each accepted child.
Local cancellation does not prove that remote computation stopped.

The relay forwards actual requests and responses through `background_fixture.py`.
It saves requests before it forwards them.
The barrier holds the first actual successful upstream response payload before Netclaw receives that payload.
The receipt separates upstream payload arrival from local payload delivery.
This proves upstream request and response activity.
It does not prove durable upstream admission or remote effect completion.
It copies response bytes from the actual forward path.
It does not replace parent or child responses.
A nonce, arrival order, or process ID alone cannot establish child ownership.

## Local controls

```bash
python3 -m unittest discover -s evals -p test_child_run_evals.py -v
python3 -m unittest discover -s evals -p test_background_evals.py -v
# Run these commands only after the combined source and compiler window are available.
dotnet build evals/fixtures/child-runs/SessionObserver.csproj -c Release
dotnet evals/fixtures/child-runs/bin/Release/net10.0/SessionObserver.dll --protocol-controls
```

The Python controls reject foreign paths, missing commit logs, extra actions, and false artifact claims.
The C# controls use the real mapper and reject malformed acceptance, unmatched results, stale turns, and early release.
Neither control suite proves real model behavior.

## Model execution

Set `NETCLAW_CHILD_OBSERVER` to the compiled observer DLL from the exact combined source.
Use the existing provider, image, CLI, and asset variables from the eval README.
Select one exact case with `NETCLAW_EVAL_RUNS=1` for each invocation.
Use five separate fresh invocations for each critical case.
Each invocation owns its container, home, session, relay, recorder, and archive.
Multiple turns in one trial retain its one connection and container.

| Case | Required evidence |
| --- | --- |
| `child_run_held_parent` | Acceptance, bound child request, independent parent status/log reply, later result, and actual artifact read |
| `child_run_partial_cancel` | Confirmed partial write, bound request, parent cancellation, cancelled evidence, and no later child request |
| `child_run_cli_acceptance` | Actual CLI acceptance matches the model tool result and a committed accepted record |

`run-background-evals.sh` owns these cases.
The existing queue-grant and shell lifecycle paths stay separate.
The CLI smoke proves initial acceptance only.
The persistent cases prove later result observation separately.

The six affected cases in `run-evals.sh` use the persistent consumer.
Select each case explicitly with one fresh invocation.
The adapter emits an eval response envelope; it does not claim to emit the CLI envelope.
The response field contains the actual final parent reply.
Each prompt owns a fresh nonce, prompt digest, input file, and receipt directory.
The final-response assertion reads only the latest prompt directory.
A failed latest prompt cannot select an earlier successful receipt.
The raw receipt retains all earlier parent replies.

The worktree handoff case has four prompts.
Prompts 1, 2, and 4 require one parent turn; they do not require child acceptance.
Prompt 3 requires acceptance and actual later terminal consumption.
An observer failure fails the case before its assertions.
A tool call ID identifies one pending occurrence; later completed occurrences can reuse that ID.
Text output labels canonical provider-history results with `[child:result]`.
The adapter never invents a second result for the original start call.
The old shell/project/artifact assertions still apply.
The adapter takes child log paths from canonical terminal data.

## Evidence and limits

The archive retains observer inputs, canonical DTOs, provider bytes, snapshots, and trial receipts under `child-runs/`.
The oracle requires these distinct post-commit diagnostics:

- `child_run_accepted`
- `child_run_terminal_recorded`
- `child_run_result_prepared`
- `child_run_delivery_admitted`, with the actual input and terminal call identifiers

A terminal call/result pair must occur in the actual parent provider request.
Its source operation must match the actual original start call.
Repeated copies of that pair in later request history count as one delivery.
A later turn alone cannot establish admission or terminal attribution.
No private journal decoder, test endpoint, or provider-capacity controller exists here.

The fixed partial-cancel case requires a successful framework report.
Its exact checkpoint must confirm the partial artifact and its actual tool result.
The actual report must match the run, state, summary, and confirmed activity.
Explicit report-grace failures remain separate outcomes; they cannot pass this fixed success case.

A foreign child with the same nonce can reach the sole candidate barrier.
The exact path bind rejects that child.
This fixed one-child relay does not prove selection among several candidates.
The separate multi-child gate remains open.

The current source preparation does not prove a build or a model pass.
The combined runtime must supply the post-commit result-preparation diagnostic.
The final stack still needs the canonical build, protocol controls, and fresh model trials.
Routed-start, approval-lifetime, recovery, and coordination cases retain their separate acceptance gates.
