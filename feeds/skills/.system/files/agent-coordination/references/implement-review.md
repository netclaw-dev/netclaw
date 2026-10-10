# Implement Then Review

Use this workflow for substantial code work with useful independent review.
Follow the common scope, authority, workspace, cancellation, and delivery rules in the base skill.

Apply task-specific command limits before the steps below. Preserve those limits in each child assignment.
An exact list permits its own operators. Do not combine separate listed commands.
When no exact list exists, use the authorized tools that the workflow requires.

1. Record the accepted behavior, source revision, preserved behavior, and required checks.
2. Use the supplied checkout state. Inspect missing state only through task-permitted commands and without changes.
3. Reuse supplied worktrees. If isolation is absent, prepare an authorized worktree only through task-permitted commands.
4. Assign one available `task-worker` the implementation scope, permitted files, worktree, and acceptance checks.
5. Give the worker a distinct report path for patch details, test evidence, known effects, and incomplete work.
6. Start other writers only in separate authorized worktrees with distinct scope.
7. Read the terminal result and inspect the exact candidate revision and patch.
8. Confirm that the worker stops source edits before independent review.
9. Assign a separate read-only reviewer the candidate revision, original acceptance checks, and worker evidence.
10. Require defect evidence, preserved-behavior checks, authority concerns, and explicit proof gaps.
11. Use an authorized isolated workspace for review commands that create build output.
12. Resolve review findings. If the patch changes, verify the new revision and update any stale review.
13. Check the patch before integration through the existing authorized repository workflow.
14. Report code changes, local checks, model evals, CI, and release state separately.

If no permitted tool can obtain a required fact, report that constraint.
If the task supplies an exact command list, do not add shell commands.
Preserve dirty operator checkout changes. Do not reset or stash them to prepare the task.
If isolation is unavailable, serialize permitted edits or report the constraint.
The reviewer must not edit the candidate patch.
A review approval is not permission to push, merge, deploy, or replace operator files.

Positive example: a worker edits one worktree; a distinct reviewer inspects the finished revision and its defect test.
Negative example: two children edit different files in one dirty checkout without shared-write authorization.
