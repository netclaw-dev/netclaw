## 1. Contract and Dependency

- [x] 1.1 Add the native Windows shell priority to `IMPLEMENTATION_PLAN.md` and
  link this OpenSpec change.
- [x] 1.2 Update the central ShellSyntaxTree package to `0.3.0-alpha.5` through
  the package-management workflow.
- [x] 1.3 Archive `adopt-shellsyntax-alpha2-pwsh` with `--skip-specs` so its
  obsolete cross-language requirement cannot enter the canonical spec.
- [x] 1.4 Replace stale POSIX PowerShell child-wrapper guidance with the
  canonical host boundary.

## 2. Canonical Environment Foundation

- [x] 2.1 Add the immutable shell environment with platform, executable,
  grammar, path style, command arguments, and optional PowerShell dialect.
- [x] 2.2 Add a deterministic Windows resolver that prefers compatible
  `pwsh.exe`, falls back to PowerShell 5.1, stores the probed absolute path, and
  fails when neither host matches.
- [x] 2.3 Add version, priority, probe-failure, startup-failure, parser-options,
  and process-argument tests for Bash, PowerShell 7.6, and PowerShell 5.1.
- [x] 2.4 Deliver the additive foundation as an adversarially reviewed PR with
  green CI and auto-merge before activation work starts.

## 3. Atomic Runtime Activation

- [x] 3.1 Remove Netclaw's POSIX `pwsh -Command` child-payload parser and pin
  both cross-language non-delegation directions.
- [x] 3.2 Route shell analysis and approval matching through the environment's
  parser, working directory, PowerShell dialect, and unknown initial-state mode.
- [x] 3.3 Route hard deny, protected paths, trust zones, safe verbs, approval
  candidates, and approval display through the environment-bound analysis.
- [x] 3.4 Add PowerShell hard-deny coverage for process termination, recursive
  root removal, and `Start-Process -Verb RunAs` before approval evaluation.
- [x] 3.5 Make buffered and streaming `ShellTool` execution use one shared
  process-start builder. It must use only the selected absolute host path and
  fixed non-interactive arguments. It must fail visibly and must not use a
  per-call fallback.
- [x] 3.6 Register one environment instance for the parser, policy, matcher,
  executor, context provider, direct calls, and background-job calls.

## 4. Model Context and Guidance

- [x] 4.1 Add platform, executable, grammar, and dialect to the Personal
  working-context tail, including sessions without a project directory.
- [x] 4.2 Prove that parent and child runs receive the same shell identity and
  that Team or Public sessions gain no shell capability.
- [x] 4.3 Update the embedded operations guidance with native Bash and
  PowerShell examples. State that ambient profiles, modules, and lookup remain
  outside parser proof.

## 5. Security and Approval Evidence

- [x] 5.1 Replace `cmd.exe` host rows with reviewed PowerShell 7.6 and Windows
  PowerShell 5.1 allow, prompt, deny, safe-verb, and stored-grant rows.
- [x] 5.2 Add parser-boundary cases for ordinary Bash `pwsh` arguments,
  ordinary PowerShell `bash` arguments, and same-language child recursion.
- [x] 5.3 Add incomplete, dynamic, unknown-dialect, 5.1 pipeline-chain,
  protected-path, redirect, provider-drive, alias, and hard-deny cases.
- [x] 5.4 Prove that a dialect change reparses before grant matching and that a
  stored approval cannot bypass a changed canonical candidate.
- [x] 5.5 Prove buffered, streaming, direct, sub-agent, background, retry, and
  redrive paths use the same selected environment and approval decision.

## 6. Verification and Delivery

- [x] 6.1 Run focused security, actor, context, buffered-executor,
  streaming-executor, resolver, and approval matrix tests on Linux and native
  Windows.
- [x] 6.2 Run restore, Release build, the full test suite, format verification,
  header verification, Slopwatch, `git diff --check`, and strict OpenSpec
  validation.
- [x] 6.3 Run the shell-platform behavioral evaluation when provider
  credentials are available. Record an unavailable provider as blocked, not
  passed.
- [x] 6.4 Run an adversarial review for every implementation PR, enable
  auto-merge only after the review passes, and follow required CI to merge.
- [x] 6.5 Update `IMPLEMENTATION_PLAN.md`, user guidance, and review-table
  evidence with observed results.
- [x] 6.6 Run `openspec-verify-change`, sync the capability deltas, and archive
  this change only after all runtime and downstream acceptance gates pass.
