# Analyze Then Plan

Use this workflow for architecture analysis and a complete plan.
Follow the scope, authority, workspace, cancellation, and delivery rules in the base skill.

1. Identify the user's objective, source revision, constraints, and open decisions.
2. Assign a read-only source analysis task to an available analyst.
3. Require source paths, revisions, component owners, evidence, risks, and unknowns.
4. Continue independent work after acceptance. Read the later result and its complete artifact.
5. Check the findings against the source. Keep observed facts separate from accepted requirements.
6. Assign a complete plan to an available worker whose current mission fits.
7. Supply the reviewed findings, accepted objective, source identity, scope, and acceptance checks.
8. Require actions, owners, verification, risks, dependencies, and unresolved choices.
9. Read the complete plan and verify its claims before user delivery.
10. Deliver the requested file through normal parent tools and policy.

Use the optional `assets/findings.md` and `assets/plan.md` when their structure helps.
Read resources through `skill_read_resource` with the canonical skill name.
If the user requires a format, preserve that format in the child assignment.
A summary or incomplete template does not satisfy a complete-plan request.
Keep unresolved consequential choices visible to the user.

Positive example: the analyst cites the cancellation owner; the plan links a failure check to that evidence.
Negative example: a worker leaves placeholders, and the parent calls the plan complete.
