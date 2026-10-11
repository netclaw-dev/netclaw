## Why

An oversized tool result had no `tool_output_read` continuation when the session workspace folder did not exist. Only a shell launch and a few file tools created that folder. In a session with no earlier shell call, the first oversized result of another tool (for example `skill_load`) gave the model a bounded window with no call ID, and Netclaw kept no text. The agent then read the physical skill files with shell and file tools. Source: PRD-001 (tool execution in the MVP session loop) and PRD-006 (MCP tool results use the same bound).

## What Changes

- The dispatcher creates the session workspace folder when it is missing, before it writes a spill. The first oversized result of each tool in each bound session gets a continuation call ID.
- When Netclaw cannot keep the full result, the result text says so. It does not offer a call ID. It does not direct the model to shell, grep, or `file_read`.
- The dispatcher writes a warning to the log, `tool_output_spill_not_retained`, with a reason.

In scope for MVP: the spill of the dispatcher for the main session and for sub-agents. Out of scope: a check of a link above the session folder, one shared creator for the workspace folder, and the removal of old spill files.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `bounded-tool-output`: one added requirement for a session with no workspace folder and for a spill that Netclaw cannot keep.

## Impact

- Code: `ToolOutputSpill`, `ToolOutputSpillLocation`, `DispatchingToolExecutor` (logger argument).
- Skill text: `netclaw-operations`, section "Large tool output".
- Evals: one new case, `complex_skill_spill_steer_single_turn`.

### Security and operational impact

- The created folder is the session workspace, the same folder that a shell launch creates. No audience gets a new root. `tool_output_read` still reads only by call ID inside the session of the caller, and the audience profile still controls whether the tool is available.
- A link at the session folder path gets no spill. No check covers a link above the session folder; the shell launcher has the same limit.
- A session with no shell call now keeps spill files on disk. No retention job deletes session folders, so these files stay until an operator removes the session folder.
- Operators get one warning record for each result that Netclaw did not keep.
