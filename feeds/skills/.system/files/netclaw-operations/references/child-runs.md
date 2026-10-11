# Background Child Tasks

Use a child for useful independent work within the current audience and profile policy.
Give it a precise objective, source context, authorized workspace, output artifact, and acceptance checks.
Use the optional `context` argument to specialize an existing profile.
Do not infer capabilities from a profile name. Check `[available-subagents]`.

## Start And Continue

`spawn_agent` and a skill that routes to a child return durable acceptance before task completion.
The first tool response contains this JSON shape:

```json
{"run_id":"<owner run ID>","scope_id":"<owner child scope ID>","state":"Accepted","control_tool":"check_agent_run"}
```

Keep the returned identifiers. Do not derive them from child text or actor names.
Acceptance proves admission. It does not prove a successful task or a current live state.
Continue independent parent work while the child executes.
The owner later delivers one terminal result with child attribution and a fresh result correlation.
That result does not replace the original acceptance response.

Ordinary parent input, start-tool completion, and start-tool timeout do not cancel an accepted child.
The child retains its original requester, audience, working scope, and operation policy.
The inference backend owns capacity and request queues. Netclaw reserves no parent or child slots.

## Status And Cancellation

Call `load_tool` with the exact name `check_agent_run` when status or cancellation is necessary.
Use the loaded schema and the returned run identifier.
Avoid tight status polls. Normal completion uses the later terminal result.

The control requires the owning parent session and original eligible requester under current policy.
A known identifier grants no access. Children and other sessions cannot inspect or cancel that run.
Use ordinary file tools for permitted result paths. A path grants no file authority.

### Inspect A Live Child Log

Use status first when a child needs diagnosis.
An authorized `check_agent_run` response supplies the exact `log_path` and `artifact_directory`, including before terminal completion.
Pass that log path to `file_read` or `file_search` under normal file policy.
Use bounded reads and targeted searches. Do not search a global log tree or derive child paths.
A log can explain a delay or show prior effects. It does not prove current health, dispatch closure, or completion.
Use the owner's recorded status and terminal result for those lifecycle facts.
Future private messages do not replace this diagnostic path.

Before reuse in eval fixtures or shared reports, scrub personal data and secrets from a derived transcript copy.
Use stable opaque replacements when correlation or equality matters. Preserve the original log unchanged.

### Cancel A Child

Set the control's cancellation argument when the task must stop.
Distinguish these facts:

- Cancellation admission is durable. The child can still have local work that must close.
- Dispatch closure confirms that no new model call, task tool, or approval retry can start locally.
- A terminal result records the final outcome and retained evidence.
- An earlier remote effect can remain unknown when its external service ignores cancellation.

An admitted invocation can delay closure before it returns its Task. The parent must still accept status requests.
Closure does not wait for that returned Task to complete.
After dispatch closure, the framework has a separate five-second deadline for finalization.
It can retain checkpoints and atomically write a report inside the assigned run artifact directory.
It cannot call a model, run task tools, edit the project, or create approval authority during that grace period.
Report failure or grace expiry preserves the last confirmed checkpoint and an explicit reason.
Do not claim that a failed report write created an artifact.
The deadline bounds the owner wait. It does not prove that an operating-system file operation stops.

Cancellation does not undo prior effects. A cancelled result is a failure outcome with useful partial evidence.
Confirmed partial evidence does not turn cancellation into successful completion.
Failed and cancelled runs do not merge a working-context delta into the parent.

## Approval Prompts

A child can request an approval prompt through its owner session when the channel supports it.
The prompt retains the original eligible requester, exact child call, authorization attempt, and accepted run.
An ordinary later parent message does not end that wait.
Cancellation or child loss expires the prompt. A late answer creates no grant or retry.
An approval prompt is a security gate. It is not a child question or a private message channel.

## Loss And Replacement

An owner restart records unresolved accepted runs as `Lost`. It does not recreate their children.
A prior durable cancellation remains cancellation. A committed terminal result retains its recorded outcome.
Read the recorded effects, unknown outcomes, and partial artifacts before you choose the next action.
The model decides whether to finish, revise the task, or request a new authorized attempt.
The framework does not replay an uncertain tool effect automatically or impose an operator-review policy.

Use cancellation and a revised child task when the instructions must change.
Private agent messages, peer discovery, and live steering are unavailable in this release.

## Workspace And Delivery

Assign one writer per workspace. Concurrent writers need separate authorized worktrees.
Preserve a dirty operator checkout. Read-only children can share source inputs and use distinct report paths.
A child report does not prove its contents. Check its revision, scope, artifact contents, and verification evidence.
Only the parent sends user messages and delivers files through `attach_file` under normal policy.
An artifact on disk does not prove delivery.
