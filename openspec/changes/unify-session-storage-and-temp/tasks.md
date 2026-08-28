## 1. Lock the Evidence Baseline

- [ ] 1.1 Record PII-free pre-change results for `subagent_session_scratch_disposable`, `approval_session_scratch_disposable`, `approval_shell_working_directory_argument`, `approval_inline_cd_semantics`, and `approval_natural_directory_change`; verify the artifact identifies the binary and keeps the prompts and assertions unchanged for the later comparison
- [ ] 1.2 Add deterministic failing contract tests for versioned single-envelope storage binding, parent-child layout, raw-log workspace exclusion, process-local temp variables, managed-temp correction eligibility, and existing-session resume; verify each test fails for the intended missing behavior before production edits
- [ ] 1.3 Add sanitized fixtures for observed parent-to-child log discovery failures and explicit POSIX temp writes; verify the PII audit finds no user, repository, channel, thread, host, email, token, or secret

## 2. Add Versioned Session Storage

- [ ] 2.1 Implement one shared atomic session-storage resolver with an optional versioned binding and use it from channel ingress, parent activation, child creation, and logging; verify concurrent first consumers receive one envelope root and no second log root
- [ ] 2.2 Change filesystem helpers to accept resolved storage paths, then implement the version-2 envelope, parent `workspace/`, named parent areas, and child-run path derivation; verify no helper writes from only a session ID plus current configuration and every new path stays below the persisted envelope
- [ ] 2.3 Leave the binding absent for existing sessions and keep their current path resolvers unchanged; verify an existing session resumes without moving, copying, or renaming data
- [ ] 2.4 Route newly bound parent and child logs below the stored envelope while leaving existing-session and daemon-global logs unchanged; verify concurrent writers use the correct main or child target
- [ ] 2.5 Keep the complete envelope and raw-log areas out of ordinary workspace-file and shell safe roots; verify the default cwd is `workspace/`, explicit traversal is re-authorized, and the tests do not claim OS-level process containment

## 3. Make Managed Temporary Storage Deterministic

- [ ] 3.1 Resolve and create one parent or child managed temporary directory before process launch; verify a preparation failure stops execution without falling back to the host temp root
- [ ] 3.2 Inject `TMPDIR`, `TMP`, and `TEMP` into every POSIX and Windows child process without changing the daemon environment; verify native and .NET temporary APIs return a path below the run's `temp_dir`
- [ ] 3.3 Add the distinct `session_dir`, `temp_dir`, and `artifact_dir` working-context entries for Personal and Team runs while preserving Public redaction; verify context snapshots contain the correct paths and purpose text
- [ ] 3.4 Use `<session-envelope>/workspace` as the shell cwd fallback when no project or explicit cwd exists; verify the complete envelope is never the fallback and the managed temporary environment operates independently

## 4. Complete the Managed-Temp Correction

- [ ] 4.1 Add the closed `UseManagedTemporaryDirectory` remediation code and presenter text; verify the model receives the exact trusted `temp_dir` and the result grants no authority
- [ ] 4.2 Detect eligible structured file writes and edits below the captured host temp root; verify denied, dynamic, external, and link-escape paths remain on their existing policy path
- [ ] 4.3 Detect exact shell redirects, explicit working directories, and canonical Bash leading-directory facts without executable-specific option parsing; verify incomplete PowerShell and private CLI syntax remain approval-gated
- [ ] 4.4 Preserve hard-deny, protected-path, audience, and noninteractive precedence; verify Public and Team calls do not receive a private path and headless calls retain their existing result
- [ ] 4.5 Extend actor-owned correction-loop keys to the eligible structured and shell forms; verify equivalent retries expose only `Once` and `Deny`, execution changes re-evaluate fully, and lifecycle boundaries clear the keys
- [ ] 4.6 Apply the same correction before the parent user bridge and child parent bridge; verify the first eligible child attempt does not prompt the parent user

## 5. Add Parent-Child Discovery Tools

- [ ] 5.1 Extend successful `spawn_agent` outcomes with the child run identifier, opaque log reference, and exact parent-readable artifact directory; verify failed spawns contain no usable child location and the result never contains a raw log path
- [ ] 5.2 Implement deferred `subagent_log_read` with parent ownership checks, bounded paging, and an optional literal query; verify foreign references are denied and no shell search is needed
- [ ] 5.3 Reuse central redaction for the child activity projection and omit prompts, credentials, secrets, raw approval payloads, and unredacted tool data; verify targeted redaction and output-limit tests pass

## 6. Add Managed Worktree Creation

- [ ] 6.1 Implement deferred `worktree_create` for an authorized current or named source repository with no caller-selected destination; verify argument-array Git execution allocates a collision-safe path below the session worktree area
- [ ] 6.2 Return canonical file activity and a typed project-scope effect only after successful worktree creation; verify failure or denial leaves project scope and existing directories unchanged
- [ ] 6.3 Record session and run ownership without adding deletion behavior; verify the worktree remains present after the session ends

## 7. Replace the Old Eval Expectations

- [ ] 7.1 Rewrite `subagent_session_scratch_disposable` so a standard temporary API must produce output below the child's `temp_dir`; verify the prompt does not name a path, cwd, environment variable, or scope tool
- [ ] 7.2 Rename `approval_session_scratch_disposable` to `approval_managed_temp_disposable` and require `file_write` plus `file_read` at `<temp_dir>/result.log` with no shell call; verify no eval describes the complete session envelope as disposable scratch
- [ ] 7.3 Keep the explicit platform-temp cases for typed `WorkingDirectory`, exact inline `cd`, and natural directory mutation; verify their requested path is preserved and normal authorization still decides execution
- [ ] 7.4 Add a correction-recovery eval for an explicit unmanaged POSIX write; verify the next authored call uses `temp_dir` and no published artifact contains PII
- [ ] 7.5 Add a parent-child handoff eval that uses the returned log reference and `subagent_log_read`; verify the parent performs no shell search for session or child logs
- [ ] 7.6 Add a managed-worktree eval that offers the focused tool through progressive disclosure; verify the agent uses `worktree_create` instead of a shell Git worktree command
- [ ] 7.7 Defer Windows model-pattern evals until sanitized representative traffic exists while keeping Windows contract tests required; verify the eval inventory records this as evidence work rather than a passed case
- [ ] 7.8 Run the locked post-change comparison with the same prompts, model configuration, and assertions as the baseline; verify the report separates deterministic acceptance from behavioral scores

## 8. Documentation and Release Verification

- [ ] 8.1 Verify the engineering glossary, update the session-storage documentation, and update the operator and eval runbooks with examples and counterexamples; verify every cross-capability term links to the glossary
- [ ] 8.2 Update the implementation plan and release notes for the versioned layout, managed temp environment, child-log tool, and worktree tool; state that pre-feature binaries resuming newly bound sessions are out of scope and verify public text contains no private provider, hardware, host, or user detail
- [ ] 8.3 Run `openspec validate unify-session-storage-and-temp --strict`, focused tests, `dotnet build -c Release`, `dotnet test -c Release`, header verification, and Slopwatch; verify every command succeeds without warning suppression or skipped tests
- [ ] 8.4 Upgrade one existing session and restart one newly bound session; verify the existing paths remain unchanged, the new envelope remains stable, ordinary workspace access excludes raw logs, and no data is moved or deleted
- [ ] 8.5 Harvest sanitized live traffic after the swap and classify remaining temp, log-discovery, worktree, and approval-friction patterns; verify new evidence enters the corpus only after manual PII review
