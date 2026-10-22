# Child Run Status And Results

A spawn or routed skill start returns after durable acceptance.
The response contains `run_id` and `scope_id`. Acceptance does not prove success.
Continue independent work. When a child ends, the runtime writes `result.md` in the run folder and admits one notice.
The notice is a user message. It names the agent, the run, the outcome, and the path of `result.md`. It holds no child output.
Read `result.md` with `file_read`. The result never arrives as tool output.
The notice joins a running turn of the same requester, or it runs in its own turn. Unfinished siblings do not delay a ready notice.

Use `check_agent_run` with the accepted `run_id` when status or intervention is necessary.
The original owner authority applies to status, cancellation, and downstream consent.
One exception covers status and cancellation: a reminder turn and any other turn of one session manage each other's child runs, when the audience, boundary, and channel type match.
The error `This child run is not available to the requester` means that the run is not yours, is in another session, or does not exist. It does not mean that the run was lost.
Status includes the lifecycle state and `pending_approval_count`.
A later ordinary parent message does not cancel the child.

## Child Consent Requests

A child can ask the operator for consent. The operator answers the prompt in the channel. You cannot answer it.
When a child consent request opens, the runtime gives you an approval notice in its own turn.
The notice starts with `Child consent requests wait for the operator.` and lists the agent, the run, and the tool.
While a request is open, the session is paused: `spawn_agent` is refused, and reminders and other automated turns wait.
A person's message still runs, and the pause stays. A slash command that a person types can still start a child.
The `spawn_agent` refusal starts with `spawn_agent is paused.` and names only your own waiting runs.
A waiting child is not stalled. It waits for the operator with no time limit.
Do not cancel, respawn, or poll a waiting child. Tell the operator to answer the prompt, or to cancel the child.
`check_agent_run` does not work in a notice turn for a run that a person started. The operator cancels in their own turn.
A notice turn cannot raise a consent prompt. A call that needs consent is denied there.
When the last request closes, a closing notice says `spawn_agent is available again.` and lists the answers.
A child prompt stays in the channel and in the TUI after the parent turn ends or fails. It stays until an answer or a close.
A child prompt has buttons only. Do not tell the operator to reply with a letter. Tell the operator to use the buttons.
A text answer such as "A" answers nothing while a child prompt is among two or more prompts that the sender can answer.
The reply is then `N approval prompts are open. Use the buttons on the prompt you mean.`
When the operator answers a child prompt in one client, the prompt in every other client shows `This approval request has an answer.`
A cancel, the end of the child, a lost child, a restart, or a prompt that the channel cannot show withdraws the request.
The prompt then shows the reason, for example `This approval request was withdrawn because the task that asked for it was cancelled. Nothing ran.`
A click on a withdrawn prompt gets the same text. Nothing runs, and no grant is written.
A restart withdraws every child consent request today. The child run is then lost.

Use `Cancel: true` to request cancellation.
The acknowledgement records intent. Wait for the notice and read `result.md` before you report completion.
Cancellation does not undo external effects. Review confirmed effects and uncertainty before a replacement task.
A late answer to an expired child prompt creates no new grant or retry.
Recovery does not relaunch an uncertain child.
An unresolved legacy run reports quarantine. It cannot execute or deliver another result.

`result.md` names the log and the artifact paths. `task.md` in the same folder records what you asked.
Use normal file tools for these exact paths. Do not derive paths or search other session trees.
A log does not prove current health or terminal completion.
A `child_run_result` with the outcome `partial` and the reason `tool_cycle_stopped` means that the child stopped before it finished. Its output can be an earlier, incomplete text. Do not treat it as a complete answer.
If evidence preparation fails, the result retains a warning and does not adopt the raw context delta.
There is no private child-message, peer-message, or live task revision tool.
For a revised assignment, request cancellation and then submit the revised task when safe.
