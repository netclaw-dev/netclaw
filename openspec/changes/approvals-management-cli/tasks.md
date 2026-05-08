## 1. Storage layer

- [x] 1.1 Add `RemoveApproval(TrustAudience audience, string toolName, string pattern)` to `ToolApprovalStore` returning a bool indicating whether an entry was removed; comparisons under the same comparer used by `ApprovalPatternMatching`.
- [x] 1.2 Add `RemoveAllForTool(TrustAudience audience, string toolName)` to `ToolApprovalStore` returning the count of entries removed; cleans up empty per-tool and per-audience maps.
- [x] 1.3 Add `Snapshot()` to `ToolApprovalStore` returning a deep-cloned `ToolApprovalData` for read-only iteration.
- [x] 1.4 Expose the platform comparer (or a `PatternsEqual(left, right)` helper) from `ApprovalPatternMatching` so the store can use it without duplicating the platform check.
- [x] 1.5 Add round-trip unit tests in `Netclaw.Configuration.Tests` (or wherever `ToolApprovalStore` already has tests) covering each new method, including platform-correct case sensitivity and empty-section cleanup.

## 2. Single-shot CLI surface

- [x] 2.1 Create `src/Netclaw.Cli/Approvals/ApprovalsCommand.cs` with a static `RunAsync(string[] args, NetclawPaths paths, TextWriter? output = null)` entry mirroring `ProviderCommand`.
- [x] 2.2 Implement `list` subcommand with `--audience`, `--tool`, and `--json` flags. Stable ordering (audience wire-value order, then tool name alpha, then pattern alpha). Empty state prints `No persistent approvals.` and exits 0.
- [x] 2.3 Implement `revoke <pattern>` with `--audience`, `--tool` flags. Exact-match removal across all audiences/tools by default; scoped when flags are provided. No match → exit 1 with clear message.
- [x] 2.4 Implement `revoke --tool <name> --all` (with optional `--audience`). Reject `--all` without `--tool` (exit 1, usage message).
- [x] 2.5 Implement `help` and surface it for `--help` / `-h` / unknown subcommand.
- [x] 2.6 Surface `.invalid` quarantine condition: when the CLI sees a sibling `.invalid` file next to `tool-approvals.json`, print a one-line warning before list/revoke output.

## 3. TUI surface

- [x] 3.1 Create `src/Netclaw.Cli/Tui/ApprovalsManagerViewModel.cs` mirroring `ProviderManagerViewModel`. State enum: `Loading`, `List`, `RevokeConfirm`. Reactive properties for current state, status message, and the list of display items grouped by audience+tool.
- [x] 3.2 Create `src/Netclaw.Cli/Tui/ApprovalsManagerPage.cs` mirroring `ProviderManagerPage`. List view shows audience → tool → pattern. Up/Down to navigate, Delete (or `r`) to enter revoke-confirm, Enter to confirm, Esc to back out, `q` to quit.
- [x] 3.3 Page reads via `ToolApprovalStore.Snapshot()` and refreshes after every revoke.
- [ ] 3.4 Manual smoke: launch from a worktree, exercise revoke path against a seeded `tool-approvals.json`, confirm file changes match a subsequent `list` output.

## 4. CLI dispatch wiring

- [x] 4.1 Add `"approvals"` to `CliArgsParser.KnownCommands`.
- [x] 4.2 Add `if (mode is "approvals")` block in `Program.cs` patterned after the `provider` block: bare-args path constructs `Host.CreateApplicationBuilder`, registers `ApprovalsManagerPage`/`ApprovalsManagerViewModel` via `AddTermina("/approvals", ...)`, configures Termina file tracing; subcommand path calls `await ApprovalsCommand.RunAsync(args, paths)` and propagates the exit code via `Environment.ExitCode`.
- [x] 4.3 Treat `netclaw approvals tui` as an alias for the bare invocation.

## 5. Tests

- [x] 5.1 `src/Netclaw.Cli.Tests/Approvals/ApprovalsCommandTests.cs` with `DisposableTempDir` fixture (mirroring `ProviderCommandTests`).
- [x] 5.2 Cover: empty file → list prints empty message + exit 0.
- [x] 5.3 Cover: list with seeded entries → table contains expected lines.
- [x] 5.4 Cover: list `--json` → JSON parses, has the documented audience/tool/patterns shape.
- [x] 5.5 Cover: list `--audience personal --tool shell_execute` → only filtered entries.
- [x] 5.6 Cover: revoke exact match → entry removed, file rewritten, exit 0.
- [x] 5.7 Cover: revoke no match → file unchanged, exit 1, message printed.
- [x] 5.8 Cover: revoke `--tool shell_execute --all` → all shell_execute entries removed across audiences (or scoped audience if specified), other tools untouched.
- [x] 5.9 Cover: revoke `--all` without `--tool` → exit 1, file unchanged, usage message.
- [x] 5.10 Cover: revoke unknown audience flag → exit 1, file unchanged, message.

## 6. Skill update

- [x] 6.1 Edit `feeds/skills/.system/files/netclaw-operations/SKILL.md` Approval Prompts section to recommend `netclaw approvals list / revoke` instead of hand-editing JSON. Keep the file-edit fallback documented as a last-resort recovery path.
- [x] 6.2 Bump `metadata.version` from `1.26.0` to `1.27.0` in the YAML frontmatter. Do NOT run `generate-skill-manifest.sh`.

## 7. Quality gates

- [x] 7.1 `dotnet build` clean.
- [x] 7.2 `dotnet test` passing for `Netclaw.Configuration.Tests` and `Netclaw.Cli.Tests`.
- [x] 7.3 `dotnet slopwatch analyze` reports zero new violations.
- [x] 7.4 `pwsh ./scripts/Add-FileHeaders.ps1 -Verify` passes.

## 8. Verification

- [x] 8.1 End-to-end: fresh `NETCLAW_HOME`, `dotnet run --project src/Netclaw.Cli -- approvals list` → empty-state message, exit 0.
- [ ] 8.2 End-to-end: seed an entry via a session "Approve always", run `netclaw approvals list`, observe the entry; revoke it; re-run the session and confirm the daemon prompts again without restart.
- [ ] 8.3 End-to-end: run `netclaw approvals` (no args) → TUI renders, navigation and revoke flow work, file changes match a follow-up `list`.
- [ ] 8.4 Run `netclaw doctor` post-mutation → `ToolAudienceProfilesDoctorCheck` still parses the file.

## 9. Companion docs issue

- [ ] 9.1 Open the implementation PR against `netclaw-dev/netclaw` closing issue #921.
- [ ] 9.2 File a tracking issue against `netclaw-dev/netclaw-website` for documenting the `netclaw approvals` CLI, cross-linking the PR, issue #921, and the `tool-approval-gates` capability.
