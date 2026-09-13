## 1. Child Working Context

- [x] 1.1 Assemble a Personal/Team-only subagent session block from the existing bound child session directory and append it to the initial volatile working context without changing `SubAgentDefinition` or persistence.
- [x] 1.2 Add deterministic Personal, Team, and Public prompt tests that prove the exact path is present only for eligible audiences and the instruction preserves explicitly required platform-temp work.
- [x] 1.3 Prove a successful child `set_working_directory` call refreshes project context without changing or duplicating the bound session scratch context.

## 2. Delegated Alignment Eval

- [x] 2.1 Remove the prescribed scratch answer from the existing parent-only disposable-output eval while retaining exact tool-argument assertions.
- [x] 2.2 Add a fixture subagent and delegated task that request disposable multi-command work without naming a path, cwd, scratch, temp root, or project declaration.
- [x] 2.3 Assert from child logs that every expected shell call passes the exact bound session directory as `WorkingDirectory`, succeeds, avoids platform temp, and returns the expected diagnostic result.
- [x] 2.4 Retain a separate explicit platform-temp eval whose authored path remains unchanged under ordinary headless authorization.

## 3. Security and Delivery Gates

- [x] 3.1 Add a headless authority regression proving session-path knowledge alone does not cover a prompt-worthy shell call.
- [x] 3.2 Run strict validation for this change and `redirect-shared-temp-to-session-scratch`, Bash syntax checks for the eval harness, focused actor/prompt tests, and the changed eval assertions.
- [x] 3.3 Run the full Release build, tests, headers, formatting, diff, PII, and changed-file Slopwatch gates; record any model eval intentionally waived rather than marking it complete.
- [x] 3.4 Update `IMPLEMENTATION_PLAN.md` with the post-0.26.0 evidence link, observed subagent prompt cluster, implementation outcome, and actual eval status.
