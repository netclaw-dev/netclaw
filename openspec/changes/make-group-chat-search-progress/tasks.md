## 1. Fair metadata progress

- [x] 1.1 Queue user and chat pages fairly; verify a later user can match before an earlier user's pages finish.
- [x] 1.2 Return cumulative work counts without member expansion; verify request shape, counts, deduplication, and repeated-page failures.
- [x] 1.3 Preserve successful checkpoints after cancellation or failure; verify cursor ownership, expiry, and bounded requests.

## 2. Automatic TUI search

- [x] 2.1 Continue batches automatically and retain matches; verify partial and full titles find results after several empty batches.
- [x] 2.2 Add stop/resume and run bounds; verify cancellation, result selection, stale responses, and unchanged canonical-ID persistence.
- [x] 2.3 Verify typed and pasted input, progress, and control visibility through headless terminal tests and the native Teams tape.

## 3. Guidance and validation

- [x] 3.1 Update the Teams guide and versioned operational skill; verify search, progress counts, stop/resume, and consent guidance agree.
- [x] 3.2 Validate this change with strict OpenSpec validation; verify it supersedes the prior manual continuation requirement.
- [ ] 3.3 Run the CLI suite, native smoke, formatter, Slopwatch, header checks, and behavioral evals; record each result.

## Validation status

- The final full CLI suite passes: 1,553 passed, zero failed, and two existing `UpdateCommand` platform/root-permission tests skipped.
- This suite includes the final TUI action-failure cases and headless typed/pasted input, progress, and control tests.
- The earlier focused Teams suite passes: 179 passed and zero failed.
- The Release build passes for the CLI project references and daemon.
- The final Slopwatch run reports zero issues.
- Strict OpenSpec validation passes.
- The operations skill passes the skill frontmatter validator at version `2.65.7`.
- The existing Teams assertion script passes `bash -n`; the tape adds an automatic-search hint anchor.
- `git diff --check` passes.
- The whitespace formatter check passes for the new integration-test file only.
- The actual PowerShell header check passes: all files have headers.
- Behavioral evals stop before execution because Docker is unavailable.
- Native tape execution remains pending. The current environment rejects Unix socket creation with `EPERM`; IPv4 loopback works.
- Full project formatting remains pending. The MSBuild formatter cannot create its required Unix socket and its host exits before completion.
