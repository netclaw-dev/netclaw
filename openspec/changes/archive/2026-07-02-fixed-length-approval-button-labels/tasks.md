## 1. Policy layer: drop dynamic label override

- [ ] 1.1 In `src/Netclaw.Actors/Tools/ToolAccessPolicy.cs` `CheckApprovalGate`, delete the dynamic-label block (currently around lines 313-336) that overrides `sessionLabel`/`alwaysLabel` based on `directoryRoots`. Replace with unconditional assignments to `ApprovalOptionKeys.ApproveSessionLabel` and `ApprovalOptionKeys.ApproveAlwaysLabel`.
- [ ] 1.2 Remove the now-unused `IsRelativeDisplayPath` helper from the same file.
- [ ] 1.3 Verify `directoryRoots.Select(static x => x.DisplayPath)` continues to flow into `ToolApprovalContext.DirectoryRoots` — no change to the context construction.

## 2. Channel adapters: document platform button-text caps

- [ ] 2.1 In `src/Netclaw.Channels.Slack/SlackApprovalBlockBuilder.cs` near the `Button { Text = new PlainText(option.Label) }` construction, add a single-line comment noting that Slack `PlainText` button text is hard-capped at 76 characters and that safe defaults live in `ApprovalOptionKeys`.
- [ ] 2.2 In `src/Netclaw.Channels.Discord/DiscordApprovalPromptBuilder.cs` near the `DiscordButtonSpec(... Label: option.Label ...)` construction, add a single-line comment noting Discord's 80-character button label cap and the same `ApprovalOptionKeys` reference.

## 3. Tests: flip existing assertions

- [ ] 3.1 In `src/Netclaw.Actors.Tests/Tools/ToolApprovalGateTests.cs`, replace the `Assert.StartsWith("Approve shell access in ", ...)` and the multi-root / absolute-root label assertions (lines 789, 791, 805-806, 832, 840) with `Assert.Equal` against `ApprovalOptionKeys.ApproveSessionLabel` / `ApprovalOptionKeys.ApproveAlwaysLabel`. Keep all assertions on `ApprovalContext.DirectoryRoots` intact.
- [ ] 3.2 In `src/Netclaw.Actors.Tests/Sessions/ParentSessionApprovalBridgeTests.cs` (lines 53-54), flip the two `Assert.Equal` calls to use `ApprovalOptionKeys.ApproveSessionLabel` / `ApprovalOptionKeys.ApproveAlwaysLabel`.
- [ ] 3.3 In `src/Netclaw.Actors.Tests/Channels/DiscordApprovalPromptBuilderTests.cs` (lines 17-18, 29-30, 44-45), change `sessionLabel` / `alwaysLabel` constants to the fixed defaults; preserve the test's intent (button-per-option, label echoed in prompt).

## 4. Tests: regression pin for long paths

- [ ] 4.1 Add a new test in `ToolApprovalGateTests.cs` that exercises a directory path longer than 76 characters and asserts every `Options[*].Label` is ≤21 characters AND equals one of the `ApprovalOptionKeys.*Label` constants. This is the regression pin for the Slack `invalid_blocks` scenario from issue #931.

## 5. Quality gates

- [ ] 5.1 `dotnet build` — succeeds.
- [ ] 5.2 `dotnet test` — all updated tests pass; the new long-path regression test passes.
- [ ] 5.3 `dotnet slopwatch analyze` — no new violations.
- [ ] 5.4 `./scripts/Add-FileHeaders.ps1 -Verify` — clean.
- [ ] 5.5 `openspec validate fixed-length-approval-button-labels` — clean.

## 6. End-to-end verification

- [ ] 6.1 In a `Personal`-posture session with `Personal.ApprovalPolicy.ToolOverrides.shell_execute = "Approval"`, ask the agent to run a shell command inside a deeply-nested project path (>76 chars). Confirm the Slack approval prompt posts successfully (no `invalid_blocks`, no auto-deny in `~/.netclaw/logs/daemon-*.log`), buttons read `Approve once` / `Approve for this chat` / `Approve always` / `Deny`, and the body still shows the directory root under `*Directory Roots*`.
- [ ] 6.2 (Optional) Repeat against a Discord-bound session if available.

## 7. Sync and archive

- [ ] 7.1 `/opsx-verify fixed-length-approval-button-labels` — implementation matches spec.
- [ ] 7.2 `/opsx-sync fixed-length-approval-button-labels` — propagate the spec delta into `openspec/specs/tool-approval-gates/spec.md`.
- [ ] 7.3 `/opsx-archive fixed-length-approval-button-labels` — archive once the change is merged.
