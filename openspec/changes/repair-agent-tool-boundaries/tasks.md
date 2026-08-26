## 0. Specification clarity

- [x] 0.1 Define shared tool, actor, path, authority, and output terms in the engineering glossary.
- [x] 0.2 Add end-to-end pseudocode and concrete examples for the path and receipt boundaries.
- [x] 0.3 Add positive examples and counterexamples for every modified specification, including tool leases, recursive-child denial, policy reasons, canonical activity, removed tools, and spill behavior.

## 1. Security and receipt boundaries

- [x] 1.1 Reject a relative project base that has a symlink or junction ancestor below its allowed root.
- [x] 1.2 Add POSIX and native Windows tests for ancestor links, direct links, traversal, and stale-project fallback.
- [x] 1.3 Move terminal authorization classification into the shared dispatcher receipt boundary.
- [x] 1.4 Prove parent and child policy denials both produce `access_denied` without successful activity.
- [x] 1.5 Keep approval requests non-terminal and prove that an approved retry can execute.
- [x] 1.6 Replace free-form remediation with a closed enum and reject undefined or wrong-category values.
- [x] 1.7 Apply a project receipt only when `set_working_directory` produced it successfully.
- [x] 1.8 Run focused security, dispatcher, parent, child, header, and Slopwatch gates.

## 2. Remove bulk tools

- [x] 2.1 Remove `JsonReadTool`, `FileReadManyTool`, their schemas, and their registration paths.
- [x] 2.2 Remove both tool names from audience profiles, core snapshots, indexes, prompts, and system skills.
- [x] 2.3 Replace valid batch intent with parallel bounded `file_read` coverage.
- [x] 2.4 Remove JSON projection product fixtures and update all evidence digests.
- [x] 2.5 Update tool footprint evidence and schema snapshots for the reduced core.
- [x] 2.6 Remove or replace the two tool-specific eval scenarios.
- [x] 2.7 Run focused tool, actor, schema, fixture, header, and Slopwatch gates.

## 3. Repair rollout contracts

- [x] 3.1 Replace the canonical raw spill path and grep steer with opaque `tool_output_read` guidance.
- [x] 3.2 Keep runtime and tests from revealing a raw spill path in model-facing results.
- [x] 3.3 Clarify that `load_tool` controls schema exposure and never grants execution authority.
- [x] 3.4 Tell agents to load a known exact tool name without a prior search.
- [x] 3.5 Make the subagent catalog replay create and inspect a real child actor.
- [x] 3.6 Keep public evidence aggregate and PII-free.
- [x] 3.7 Run focused spill, disclosure, subagent, skill, header, and Slopwatch gates.

## 4. Stack and evaluation

- [x] 4.1 Rebase each branch on its intended parent and verify the stacked patch order.
- [x] 4.2 Create three stacked pull requests without auto-merge.
- [x] 4.3 Run the deterministic eval harness on the final stacked head.
- [x] 4.4 Run the hosted eval matrix on the final stacked head.
- [x] 4.5 Publish only PII-free aggregate eval results on the relevant pull request.
- [x] 4.6 Run the full build, tests, strict OpenSpec, headers, diff, and Slopwatch gates.
