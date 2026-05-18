## 1. Verb-chain depth-1 cap

- [x] 1.1 In `src/Netclaw.Security/ShellTokenizer.cs`, add a new
  `SingleTokenCommandVerbs` set (sibling of `PathAwareVerbs`) holding the new
  single-token command verbs (`date`, `ps`, `which`, `uname`, `uptime`, `free`,
  `id`, `hostname`, `whoami`, `groups`, `printenv`, `nproc`, plus Windows
  `Get-Date`/`Get-Process`/`Get-ComputerInfo`) and have `ApplyVerbShortCircuit`
  consult it. `env` is excluded — it can prefix an arbitrary command.
- [x] 1.2 Add/extend `ShellTokenizer` verb-chain extraction tests proving
  `date +%Y-%m-%d` → `date`, `ps aux` → `ps`, `uname -a` → `uname`,
  `which ilspycmd` → `which` extract to the bare verb.

## 2. Expand the bundled safe-verb lists

- [x] 2.1 In `src/Netclaw.Configuration/SafeVerbs/safe-verbs.linux.json`, add
  read-only system/info verbs (`date`, `whoami`, `id`, `groups`, `hostname`,
  `uname`, `uptime`, `free`, `ps`, `printenv`, `nproc`, `basename`,
  `dirname`, `realpath`, `readlink`; `env` excluded — command-prefixing),
  read-only git subcommands
  (`git describe`, `git rev-list`, `git cat-file`, `git shortlog`), and
  read-only gh queries (`gh pr view`, `gh pr list`, `gh pr checks`,
  `gh pr diff`, `gh pr status`, `gh issue view`, `gh issue list`,
  `gh run view`, `gh run list`, `gh repo view`, `gh release view`,
  `gh release list`, `gh label list`, `gh auth status`). Apply the exclusion
  bar: no verb that can write/delete files, execute arbitrary code, or
  POST/PATCH/DELETE (excludes `git tag`, `git fetch`, `gh api`, `curl`, `dotnet`).
- [x] 2.2 In `safe-verbs.windows.json`, add the Windows equivalents
  (`Get-Date`, `Get-Process`, `Get-ComputerInfo`, `whoami`, `hostname`) plus the
  identical read-only `git`/`gh` subcommands.
- [x] 2.3 Update the `$comment` block in both files to reflect the widened
  scope (still curated, still review-gated).

## 3. Tests

- [x] 3.1 Update `SafeVerbLoaderTests` — both JSON files load; the new verbs are
  present in the loaded `SafeVerbList`.
- [x] 3.2 Update `ScopedShellSafeVerbPolicyTests` — a representative new verb
  (`date`, `gh pr view`) short-circuits inside a trusted zone; still prompts
  outside one; a compound mixing a new safe verb with a mutating verb still
  prompts (all-clauses-safe conjunction holds).
- [x] 3.3 Confirm `SafeVerbList.cs` and `ScopedShellSafeVerbPolicy.cs` need no
  code change (verify-only); adjust only if a hardcoded count/assertion breaks.

## 4. Spec, skill, and docs

- [x] 4.1 Apply the `tool-approval-gates` delta spec (handled by OpenSpec
  apply/sync): corrected "Shell command pattern matching" scenario + new
  "Global grant precedence over folder-scoped grants" requirement.
- [x] 4.2 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` —
  approval-gate guidance reflects the widened auto-allow set; bump
  `metadata.version` (System Skills Sync Rule).
- [x] 4.3 Update `docs/runbooks/tool-approval-gates.md` — note the expanded
  safe-verb set.

## 5. Verification

- [x] 5.1 `dotnet build` and the affected test projects pass — full
  `Netclaw.Security.Tests` (557) and `Netclaw.Configuration.Tests` (312)
  suites green, plus `ScopedShellSafeVerbPolicyTests`, `ToolApprovalGate*`,
  and `ToolApprovalActor*` in `Netclaw.Actors.Tests` (70) green.
- [x] 5.2 `dotnet slopwatch analyze` reports no new violations (0 issues);
  `./scripts/Add-FileHeaders.ps1 -Verify` passes (all files have headers).
- [x] 5.3 Gate behavior is daemon-side and fully covered by the new unit
  tests (`SafeVerbLoaderTests`, `ScopedShellSafeVerbPolicyTests`,
  `ShellTokenizerTests`). The `./evals/run-evals.sh` suite tests model
  behavior against a Docker container + provider endpoint — operator
  infrastructure not available in this environment and not the right
  regression vehicle for a daemon-side gate change; flagged for the operator
  if an eval target is configured.
- [ ] 5.4 Operator manual check (needs a live Slack-connected daemon): in a
  Slack session, `date +%Y-%m-%d` runs with no prompt; a mutating verb still
  prompts.
- [x] 5.5 `/opsx-verify`, then `/opsx-archive` the change.
