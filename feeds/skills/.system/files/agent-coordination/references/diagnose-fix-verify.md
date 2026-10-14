# Diagnose, Fix, And Verify

Use this workflow when a defect needs reproduction, a repair, and independent proof.
Follow the common scope, authority, workspace, cancellation, and delivery rules in the base skill.

1. Record expected behavior, observed behavior, task authority, and the baseline revision.
2. Assign a scoped analyst task to reproduce the defect and identify its source boundary.
3. Require the exact input, failure evidence, environment prerequisites, and remaining uncertainty.
4. Read the evidence and confirm that the counterexample detects the stated product defect.
5. Assign a worker a narrow repair in an authorized workspace with the counterexample and preserved behavior.
6. Require the candidate revision, patch, test receipts, known effects, and incomplete work.
7. Inspect the candidate after the worker stops edits.
8. Assign a separate verifier the exact candidate, baseline counterexample, and a valid behavior control.
9. Require baseline failure for the stated defect and candidate success for that same check.
10. Require the valid control to succeed so the repair does not merely block all useful work.
11. Resolve defects and stale evidence before the parent reports the repair.
12. State any environment failure or missing proof as a verification gap.

A missing dependency does not prove a product defect or a successful repair.
A model's explanation does not replace the actual counterexample.
Use explicit acknowledgements or observable signals for async checks. Do not hide races with arbitrary delays.
Keep private logs and user data outside shared artifacts unless access and disclosure are authorized.

Positive example: the baseline dispatches after cancellation closure; the candidate rejects that dispatch and permits ordinary work.
Negative example: a missing provider prevents every task, and the parent calls the original defect fixed.
