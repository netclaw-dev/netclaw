# Child Run Status And Results

A spawn or routed skill start returns after durable acceptance.
The response contains `run_id` and `scope_id`. Acceptance does not prove success.
Continue independent work. The parent receives a ready result at the next safe model boundary.
The current user batch stays intact. Unfinished siblings do not delay a ready child result.

Use `check_agent_run` with the accepted `run_id` when status or intervention is necessary.
The original owner authority applies to status, cancellation, and downstream consent.
One exception covers status and cancellation: a reminder turn and any other turn of one session manage each other's child runs, when the audience, boundary, and channel type match.
The error `This child run is not available to the requester` means that the run is not yours, is in another session, or does not exist. It does not mean that the run was lost.
Status includes the lifecycle state and `pending_approval_count`.
A child can wait for human consent while the parent continues other work.
A later ordinary parent message does not cancel the child.

Use `Cancel: true` to request cancellation.
The acknowledgement records intent. Wait for a terminal result before you report completion.
Cancellation does not undo external effects. Review confirmed effects and uncertainty before a replacement task.
A late answer to an expired child prompt creates no new grant or retry.
Recovery does not relaunch an uncertain child.
An unresolved legacy run reports quarantine. It cannot execute or deliver another result.

The terminal result includes authorized log and artifact paths when available.
Use normal file tools for these exact paths. Do not derive paths or search other session trees.
A log does not prove current health or terminal completion.
A `child_run_result` with the outcome `partial` and the reason `tool_cycle_stopped` means that the child stopped before it finished. Its output can be an earlier, incomplete text. Do not treat it as a complete answer.
If evidence preparation fails, the result retains a warning and does not adopt the raw context delta.
There is no private child-message, peer-message, or live task revision tool.
For a revised assignment, request cancellation and then submit the revised task when safe.
