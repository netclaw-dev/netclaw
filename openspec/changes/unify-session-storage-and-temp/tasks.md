## 1. Lock the Evidence Baseline

- [x] 1.1 Record PII-free pre-change results for the unchanged managed-temp and explicit-path cases; identify the binary, prompt, tool surface, model configuration, and assertion revision
- [x] 1.2 Add deterministic contract tests for every revised acceptance boundary: storage collision resistance, journal-only legacy discovery, current-session and inherited trusted roots, root-segment link escape, old background-job JSON, composed worktree flow, and readable ordinary configuration
- [x] 1.3 Add sanitized fixtures for observed parent-to-child log discovery failures and explicit POSIX temp writes; verify the PII audit finds no user, repository, channel, thread, host, email, token, or secret

## 2. Add Versioned Session Storage

- [x] 2.1 Implement one shared atomic session-storage resolver with an optional versioned binding and use it from channel ingress, parent activation, child creation, and logging; verify concurrent first consumers receive one envelope root and no second log root
- [x] 2.2 Change filesystem helpers to accept resolved storage paths, then implement the version-2 envelope, parent `workspace/`, named parent areas, and child-run path derivation; verify no helper writes from only a session ID plus current configuration and every new path stays below the persisted envelope
- [x] 2.3 Leave the binding absent for existing sessions and keep their current path resolvers unchanged; verify an existing session resumes without moving, copying, or renaming data
- [x] 2.4 Route newly bound parent and child logs below the stored envelope while leaving existing-session and daemon-global logs unchanged
- [x] 2.5 Resolve or cache the immutable storage binding once per logging scope instead of taking a SQLite immediate transaction for each log record; keep atomic first binding
- [x] 2.6 Make physical envelope names collision-resistant for distinct raw session IDs whose sanitized forms are equal; verify persistence rejects two sessions claiming one envelope
- [x] 2.7 Detect journal-only existing sessions through the shipped `journal` schema; verify a legacy session with no snapshot does not receive a new binding
- [x] 2.8 Carry the current session envelope or established legacy roots as implicit trusted roots in parent and child invocation context; preserve `workspace/` as the default cwd
- [x] 2.9 Use one non-configurable Netclaw SQLite database for journal, snapshots, reminders, catalog data, daily statistics, memory, and storage bindings; remove the runtime custom-path and in-memory-provider configuration surface

## 3. Make Managed Temporary Storage Deterministic

- [x] 3.1 Resolve, create, and validate one parent or child managed temporary directory before process launch; reject link or reparse-point escapes and do not fall back to the host temp root
- [x] 3.2 Inject `TMPDIR`, `TMP`, and `TEMP` into every POSIX and Windows child process without changing the daemon environment; verify native and .NET temporary APIs return a path below the validated run `temp_dir`
- [x] 3.3 Extend the existing parent and child `[session]` context blocks with `temp_dir`, `artifact_dir`, `worktree_dir`, and `log_path`; preserve current `session_dir` assembly and Public path policy, and verify no second block or per-turn duplicate appears
- [x] 3.4 Use `<session-envelope>/workspace` as the shell cwd fallback when no project or explicit cwd exists; verify the complete envelope is never the fallback and the managed temporary environment operates independently
- [x] 3.5 Audit every production use of “session scratch” and classify it as session-directory fallback, managed temporary storage, or trusted-root path authorization; update model prompts, `AGENTS.md`, tool schemas, comments, and identifiers to the correct term
- [x] 3.6 Accept persisted background-job JSON with no `ManagedTemporaryDirectory`; preserve terminal history, apply the existing `Lost` transition and notification to pending or running jobs, and never resume them with host temp

## 4. Complete the Managed-Temp Correction

- [x] 4.1 Replace `UseSessionScratch` with the closed `UseManagedTemporaryDirectory` remediation code and presenter text; verify the model receives the exact trusted `temp_dir`, never `session_dir`, and the result grants no authority
- [x] 4.2 Detect eligible structured file writes and edits below the captured host temp root; verify denied, dynamic, external, and link-escape paths remain on their existing policy path
- [x] 4.3 Detect exact shell redirects, explicit working directories, and canonical Bash leading-directory facts without executable-specific option parsing; verify incomplete PowerShell and private CLI syntax remain approval-gated
- [x] 4.4 Preserve hard-deny, protected-path, audience, and noninteractive precedence; verify Public and Team calls do not receive a private path and headless calls retain their existing result
- [x] 4.5 Extend actor-owned correction-loop keys to the eligible structured and shell forms; verify equivalent retries expose only `Once` and `Deny`, execution changes re-evaluate fully, and lifecycle boundaries clear the keys
- [x] 4.6 Apply the same correction before the parent user bridge and child parent bridge; verify the first eligible child attempt does not prompt the parent user
- [x] 4.7 Rename correction, retry, approval-context, parent-actor, and child-actor symbols from `SessionScratch*` to `ManagedTemporary*` or `ManagedTemp*`; separately rename session-directory approval guards to `SessionOwned*` terminology
- [x] 4.8 Retain protobuf field 19 `session_scratch_directory` as legacy-read-only input, add `managed_temporary_directory` with a new field number, and verify recovered approvals complete without reinterpreting the old path

## 5. Compose Session Data Access from Existing File Tools

- [x] 5.1 Extend successful `spawn_agent` outcomes with the child run identifier, exact child log path, and exact child artifact directory; create the log target before success and verify failed spawns contain no usable child location
- [x] 5.2 Remove the special same-session log scope and child ownership ACL. Use the common path access decision for `file_read`, `file_list`, `file_search`, `file_write`, `file_edit`, and `attach_file`
- [x] 5.3 Supply the Netclaw sessions directory as a trusted root to every parent and child run; verify one session can analyze another session's logs when audience and operation permissions allow it
- [x] 5.4 Use an active-writer-compatible read share mode for `file_read` and `file_search`; verify active Windows and POSIX writers continue after a read
- [x] 5.5 Validate every trusted root and canonical path against symbolic-link, junction, and reparse-point escape through the common path access decision
- [x] 5.6 Inventory every filesystem path term, OpenSpec requirement, policy type, decision method, and call site; map each item to one owner and identify whether it survives, merges, or is removed
- [x] 5.7 Condense the four affected delta specs around one owning requirement, then replace competing root terms and Boolean path decisions with one shared contract; remove duplicate helpers, call sites, and tests
- [x] 5.8 Compose tool authorization as ordered capability, tool-family, file-protection, and approval layers; verify shell stops before file checks when disabled, file tools never require shell, and every admitted shell path uses conservative `Write` authority before approval

## 6. Compose Git Worktrees from Existing Tools

- [x] 6.1 Delete the `worktree_create` tool, its registration, tests, typed effect, and unused durable ownership machinery; verify the dynamic tool catalog no longer exposes it
- [x] 6.2 Announce the exact `worktree_dir` in the existing session context and document `shell_execute` plus `set_working_directory` as the supported composition
- [x] 6.3 Verify ordinary shell authorization decides Git worktree commands and that a failed or denied Git call does not change project scope

## 7. Allow Safe Reads of Ordinary Configuration

- [x] 7.1 Separate structured read-deny paths from broad shell indicators so an exact `file_read` of `netclaw.json` follows normal trusted-root and audience policy
- [x] 7.2 Keep `secrets.json`, keys, OAuth credentials, webhook secret material, SQLite state and sidecars, process-control files, and similar protected state read-denied; verify symlink and path traversal cannot bypass the deny
- [x] 7.3 Verify the existing `netclaw.json` schema has no secret-bearing fields; do not add content redaction, field heuristics, or migration to this change
- [x] 7.4 Verify readable configuration does not widen write, edit, attach, or shell authority and does not require a special configuration-reader tool

## 8. Replace the Old Eval Expectations

- [x] 8.1 Rewrite `subagent_session_scratch_disposable` so a standard temporary API must produce output below the child's `temp_dir`; verify the prompt does not name a path, cwd, environment variable, or scope tool
- [x] 8.2 Rename `approval_session_scratch_disposable` to `approval_managed_temp_disposable` and require `file_write` plus `file_read` at `<temp_dir>/result.log` with no shell call
- [x] 8.3 Keep the explicit platform-temp cases for typed `WorkingDirectory`, exact inline `cd`, and natural directory mutation; verify their requested path is preserved and normal authorization still decides execution
- [x] 8.4 Strengthen the parent-child handoff eval so it proves a successful existing file-tool call against the returned child path, not a path string or prose claim
- [x] 8.5 Replace the custom-tool worktree eval with a natural Git CLI case; require successful creation below `worktree_dir` followed by successful `set_working_directory`
- [x] 8.6 Defer Windows model-pattern evals until sanitized representative traffic exists while keeping Windows contract tests required
- [ ] 8.7 Report rewritten cases as replacement evidence. Claim direct before-and-after comparison only for identical prompts, tool surfaces, model configuration, and assertion hashes

## 9. Documentation and Release Verification

- [x] 9.1 Condense the engineering glossary, active OpenSpec text, and runbooks around the shared path access decision; remove duplicate authority terms and stale session-isolation claims
- [x] 9.2 Update the implementation plan and release notes for the versioned layout, managed temp environment, composed worktree workflow, and independent structured-read policy; verify public text contains no private provider, hardware, host, or user detail
- [x] 9.3 Remove or correct evidence that used changed prompts or assertions as a locked comparison, remove invalid headless results, and record exact evidence revisions without changing archived evidence
- [x] 9.4 Run `openspec validate unify-session-storage-and-temp --strict`, focused tests, `dotnet build -c Release`, `dotnet test -c Release`, header verification, and Slopwatch; report existing skipped tests accurately instead of claiming zero skips
- [ ] 9.5 Upgrade one existing session and restart one newly bound session; verify established and new paths remain usable, active writers remain healthy, paths stay stable, and no data is moved or deleted
- [ ] 9.6 Harvest sanitized live traffic after the swap and classify remaining temp, log-discovery, worktree, configuration-read, and approval-friction patterns; add evidence to the corpus only after manual PII review
