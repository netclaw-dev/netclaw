## Context

See [proposal.md](proposal.md). ShellTool starts normal and streamed processes directly.
BackgroundJobExecutionActor starts persisted command fields without a policy check.
The manager queues job identifiers and marks unfinished records as lost after restart.

## Goals / Non-Goals

The shared boundary owns preparation and process creation. Existing callers own the process lifetime.
This change does not add a grant store, shell parser, or durable authorization token.

## Decisions

ShellProcessLaunch retains one exact invocation and its environment snapshot.
The dispatcher supplies a required policy check that reads current grant evidence.
It copies mutable approval state so a later tool call cannot alter the queued request.
The background submission carries this call-local launch; the manager retains it only while the job waits.
Persisted definitions retain the existing format and never authorize a replay.

The launch creates one process directly after the final authority check, without another actor message or await.
It compares resolved path targets before and after the grant await. Changed path facts cause a visible denial.
The background actor receives the process through its mailbox. Cancellation remains available while policy awaits the grant service.
The start task owns the process until the actor adopts it. Actor stop cancels startup and reclaims an unclaimed process result.

The public direct ShellTool API retains its existing command and protected-path checks.
Routed calls additionally require the coordinator. Background submission cannot use an unchecked legacy path.

Ordered flow:
1. Copy the exact invocation and child environment.
2. Authorize submission through the coordinator.
3. Retain the launch while the job waits.
4. Prepare cwd and managed temporary storage at launch.
5. Recheck current authority through the coordinator.
6. Check cancellation and current command/path policy.
7. Create one process and transfer ownership to the caller.

## Risks / Trade-offs

- OS path changes can race any userspace check. Final path checks narrow that window; this change does not provide filesystem sandbox isolation.
- A queued grant can expire. The job reports a failed launch without a new interactive prompt.
- An in-process launch retains authority context. Completion, cancellation, and reaping must remove queued references.

## Migration Plan

The in-process StartBackgroundJob contract now requires ShellProcessLaunch. Its command, directory, session, and trust properties derive from that launch.
BackgroundJobExecutionActor constructors also require the checked launch. Host extensions must create it through DispatchingToolExecutor.PrepareShellLaunchAsync.
The former unchecked actor constructors cannot remain executable without the required authority context.
BackgroundJobManagerActor retains both constructor signatures. The launch now supplies its shell identity; the compatibility constructor no longer selects another shell.

No persisted format changes occur. Restart recovery continues to mark unfinished jobs as lost.
A source revert cannot undo process side effects. Deployment remains a separate operator action.

## Delivery

One PR implements T12–T14 after PR #2117. It includes source, regression tests, skill guidance, and an independent aggressive review.
Evidence must cover stale authority, path mutation, cancellation, one process, output, and detached lifetime.
