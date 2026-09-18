## 0. Specification Clarity

- [x] 0.1 Link the shared glossary and define the complete correction, exposure, authorization, and persistence flow.
- [x] 0.2 Add exact positive, negative, parent, and child examples for the changed behavior.
- [x] 0.3 Define policy visibility, native Netclaw tools, and skill resources. Add exact correction, source-order, authority, and exposure examples.

## 1. Freeze the Behavior Contract

- [x] 1.1 Validate the proposal, design, and three delta specifications in strict mode.
- [x] 1.2 Add the change to `IMPLEMENTATION_PLAN.md` with the stack parent and delivery gates.

## 2. Make Direct Attachment the Core Path

- [x] 2.1 Register `attach_file` as parent-session Core without changing its audience or path authorization.
- [x] 2.2 Rewrite the tool definition and shared guidance to require the existing authorized source path directly and forbid a preparatory shell copy.
- [x] 2.3 Update exact core-name, schema-footprint, and audience snapshots; exclude child `attach_file` search, load, and dispatch until an attachment handoff exists.

## 3. Add a Closed Native-Tool Correction

- [x] 3.1 Add `UseNativeTool` to the closed internal remediation code and one fixed shared presenter action.
- [x] 3.2 Add an internal authorization correction outcome and a closed `NativeToolSuggested` fact without changing public or durable contracts.
- [x] 3.3 Detect the first exact policy-visible first-party executable occurrence from complete static ShellSyntaxTree analysis after hard-deny preflight and before shell policy, approval, or execution.
- [x] 3.4 Exclude dynamic and unresolved identities, hidden or denied tools (including child-static-denied registrations), MCP tools, `shell_execute`, and non-exact names without parsing private tool syntax.
- [x] 3.5 Translate the correction through buffered, streaming, background, and child execution without starting a shell process or contacting approval.

## 4. Activate the Native Schema Safely

- [x] 4.1 Carry one exposure fact with the exact registered tool name in call-local messages, separate from model-facing and durable messages.
- [x] 4.2 Record the correction result before parent or child activation, then recheck registry and current exposure policy at the actor seam.
- [x] 4.3 Prove Core activation is a no-op, Deferred activation reaches the next model request, and eventual native dispatch uses normal authorization.
- [x] 4.4 Prove actor failure, recovery, and child completion do not persist or share correction-driven exposure.

## 5. Adversarial Regression Coverage

- [x] 5.1 Add Bash and PowerShell positives for bare, argument-bearing, redirected, pipeline, wrapper, and compound static occurrences.
- [x] 5.2 Add negatives for dynamic, unknown, fuzzy, path-qualified, hidden, denied, MCP, and `shell_execute` identities.
- [x] 5.3 Add protected-path and invalid-input precedence tests proving no correction or activation after a terminal preflight deny.
- [x] 5.4 Add parent batch, parent streaming, parent background, and child tests proving no shell execution, no approval request, exact shared presentation, and next-request schema exposure.
- [x] 5.5 Add direct-attachment tool-choice coverage proving no shell or file-mutation prelude is required.
- [x] 5.6 Prove actor activation rejects a now-denied or missing registration.
- [x] 5.7 Prove a compound correction prevents earlier shell side effects.

## 6. PII-Free Behavioral Evidence

- [x] 6.1 Add deterministic synthetic cases for direct attachment and shell-to-native recovery; preserve a deferred-tool discovery case after `attach_file` becomes Core.
- [x] 6.2 Run fixed baseline and treatment trials for direct attachment and shell-to-native recovery with fresh sessions.
- [x] 6.3 Publish only PII-free aggregate completion, tool-choice, shell-authored, shell-started, approval, activation, and correction-to-native metrics on the PR.

## 7. Delivery Gates

- [x] 7.1 Run Release build and full tests with zero failures and warnings.
- [x] 7.2 Run strict OpenSpec, headers, changed-file formatting, shell harness checks, diff check, and Slopwatch with zero findings.
- [x] 7.3 Perform a frozen-SHA adversarial review of authority, privacy, parent/child parity, and public/durable compatibility.
- [x] 7.4 Rebase on the refreshed #2046 head, push as the next stacked PR with GitHub CLI, apply relevant labels, and leave merge and auto-merge disabled.
