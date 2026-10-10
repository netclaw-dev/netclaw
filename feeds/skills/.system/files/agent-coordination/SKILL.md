---
name: agent-coordination
description: "Coordinate substantial code tasks, architecture plans, independent research, and defect repairs through scoped child tasks and reviewed artifacts."
metadata:
  author: netclaw
  version: "1.0.5"
---

# Agent Coordination

As the parent, use children for useful independent work without an explicit delegation request.
Use this skill for substantial code work, architecture analysis, research, and complete plan artifacts.
Complete trivial tasks directly when a child adds no useful independent scope.
You own the task scope, evidence review, user messages, and artifact delivery.

Task-specific limits take precedence over workflow preparation suggestions.
If the task gives an exact permitted-command list, use each command exactly, including its existing operators.
Do not add commands or combine separate listed commands.

## Select One Workflow

Read the selected resource with `skill_read_resource`. Do not load all workflows by default.

| Task | Resource |
|---|---|
| Analyze architecture, then produce a complete plan | `references/analyze-plan.md` |
| Research distinct questions and compare evidence | `references/parallel-research.md` |
| Implement code, then obtain independent review | `references/implement-review.md` |
| Reproduce a defect, repair it, then verify the repair | `references/diagnose-fix-verify.md` |

Use `assets/findings.md` or `assets/plan.md` through the same logical resource tool when a workflow needs its template.
Replace all required placeholders. A template is not a completed artifact.
If a result spills, use `tool_output_read` with its receipt. Do not derive physical skill paths.

## Assign A Precise Child Task

Discover profiles in `[available-subagents]`. Check their current missions before selection.
Use `task-worker` for scoped implementation and complete artifacts when its current profile permits that work.
Use `code-analyst` for source analysis or review, `research-assistant` for external evidence, and `summarizer` for concise summaries.
Do not invent a missing profile or override an operator's profile through assumptions.

Give each child:

- The concrete question or code objective.
- The source revision or supplied source identity, its stated meaning, and relevant evidence.
- The permitted workspace, read/write scope, exact command limits, and forbidden edits.
- A distinct output path and the expected complete artifact.
- The acceptance checks and required evidence.
- The facts and constraints that the child must not infer.

Copy the supplied source identity unchanged into the child assignment, including its type or algorithm prefix.
When a child must use a template, give its exact fields and columns through `Task` or `Context`.
Alternatively, require the child to load that exact canonical skill resource through `skill_read_resource`.
Name the skill and resource explicitly. Load only the resources that the assigned task needs.
Preserve supplied source identities exactly in required artifact fields.

Paths and instructions grant no authority. Every tool and file action retains normal runtime policy.
If a required capability is unavailable, report the limitation. Do not bypass audience or profile policy.

## Preserve Workspace Ownership

Assign one writer to each workspace. Concurrent writers need separate authorized worktrees.
Read-only children can share source inputs. Give them distinct report paths.
Use existing git, shell, and file tools for authorized workspace preparation.
Preserve dirty operator checkouts. Do not reset, stash, or discard their changes to prepare a task.
If isolation is unavailable, serialize permitted edits or report the constraint.
Different target filenames do not authorize concurrent writes in one checkout.
Give a separate reviewer the exact finished revision. The reviewer must not edit the candidate patch.
Commands that create build output need an authorized isolated workspace.

## Background Runs And Cancellation

`spawn_agent` returns an accepted run ID before child completion.
Continue independent parent work. The terminal result arrives later with child attribution.
Load `check_agent_run` through `load_tool` only when status or cancellation is necessary.
Do not create a tight status poll loop. Normal completion uses the terminal result.
Status and cancellation retain the owner-session and original eligible requester boundary under current policy.
For child diagnosis, read the authorized status response's exact `log_path` with ordinary file tools.
Use bounded reads and targeted searches. Follow normal file policy.
Do not derive paths or search a global log tree.
A log does not prove current health, dispatch closure, or completion.
Use recorded status and terminal evidence for lifecycle decisions.
Load `netclaw-operations` and read `references/child-runs.md` for the full diagnostic and authority rules.

A cancellation acceptance does not prove dispatch closure or terminal completion.
Check explicit closure and terminal evidence before you treat the child as stopped.
The runtime retains partial evidence through its five-second framework-only finalization path.
Inspect confirmed effects, unknown effects, and partial artifacts before a replacement task.
Cancellation does not undo prior effects. A replacement needs revised scope or a justified next attempt.
`Lost` reports child loss after restart. Do not claim automatic resume.
There are no private agent messages, child questions, peer tools, or live steering in this workflow.

## Check Evidence And Deliver

Read the complete returned artifact before you assign the next stage.
Check every required field, column, source identity, and evidence excerpt against the task and actual source.
Check the task scope and acceptance evidence.
A success flag, artifact path, or model agreement does not prove completion.
Report evidence conflicts, incomplete work, stale results, and unavailable verification accurately.
Only the parent sends user messages and delivers files through `attach_file` under normal policy.
An artifact on disk is not proof of delivery. Report a blocked attachment as a delivery limitation.
Follow the requested final response schema and field types.
Use artifact paths in path fields. Do not substitute report contents.
If the task requires JSON only, return one JSON value without prose or code fences.
