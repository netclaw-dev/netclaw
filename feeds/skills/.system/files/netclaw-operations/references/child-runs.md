# Child Run Status And Results

A spawn or routed skill start returns after durable acceptance.
The response contains `run_id` and `scope_id`. Acceptance does not prove success.
Continue independent work. When a child ends, the runtime writes `result.md` in the run folder and admits one notice.
The notice is a user message. It names the agent, the run, the outcome, and the path of `result.md`. It holds no child output.
Read `result.md` with `file_read`. The result never arrives as tool output.
The notice joins a running turn of the same requester, or it runs in its own turn. Unfinished siblings do not delay a ready notice.

Use `check_agent_run` with the accepted `run_id` when status or intervention is necessary.
The original owner authority applies to status, cancellation, and downstream consent.
Status includes the lifecycle state and `pending_approval_count`.
A child can wait for human consent while the parent continues other work.
A later ordinary parent message does not cancel the child.

Use `Cancel: true` to request cancellation.
The acknowledgement records intent. Wait for the notice and read `result.md` before you report completion.
Cancellation does not undo external effects. Review confirmed effects and uncertainty before a replacement task.
A late answer to an expired child prompt creates no new grant or retry.
Recovery does not relaunch an uncertain child.
An unresolved legacy run reports quarantine. It cannot execute or deliver another result.

`result.md` names the log and the artifact paths. `task.md` in the same folder records what you asked.
Use normal file tools for these exact paths. Do not derive paths or search other session trees.
A log does not prove current health or terminal completion.
If evidence preparation fails, the result retains a warning and does not adopt the raw context delta.
There is no private child-message, peer-message, or live task revision tool.
For a revised assignment, request cancellation and then submit the revised task when safe.
