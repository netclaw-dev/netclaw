# Analyze Then Plan

Use this workflow for architecture analysis and a complete Markdown plan.
Follow the common scope, authority, workspace, cancellation, and delivery rules in the base skill.

1. Identify the user's objective, current revision, constraints, and unresolved decisions.
2. Read `assets/findings.md` through `skill_read_resource`.
3. Assign a read-only source analysis task to an available analyst.
4. Give the analyst a distinct findings path and explicit acceptance checks.
5. Require source paths, revisions, component owners, evidence IDs, risks, and unknowns.
6. Continue independent work after acceptance. Read the later findings artifact.
7. Check evidence against the actual source. Keep claims separate from accepted requirements.
8. Read `assets/plan.md` through `skill_read_resource`.
9. Assign a complete plan artifact to an available `task-worker` whose current mission fits.
10. Give the worker the reviewed findings, accepted objective, plan path, and required structure.
11. Require finding references, actions, acceptance checks, risks, and open decisions.
12. Read the complete plan and check its traceability before user delivery.
13. Deliver the Markdown file through `attach_file` under normal policy.

Do not use the summary profile as a substitute for a complete plan task.
Do not let an unreviewed child finding become a user requirement.
An incomplete template or a summary does not satisfy a complete-plan request.
If the analysis cannot resolve a consequential choice, preserve that choice as an open decision.

Positive example: the analyst cites the actor that owns cancellation; the plan links its acceptance checks to that finding.
Negative example: the worker returns a short summary with placeholders, and the parent calls the plan complete.
