## 1. F2: scope-free data commands

- [x] 1.1 Share `IsScopeFreeDataCommand` between `TryCreateAssignmentDigest` and `ExtractCommandCandidates`; verify that `data-commands-after-failing-cd-are-exempt` and its unattended twin return `Allowed`
- [x] 1.2 Keep the exact prompt for a redirect and an unquoted unknown operand; verify `data-command-redirect-after-failing-cd-keeps-prompt` and `unquoted-unknown-echo-after-failing-cd-keeps-prompt`
- [x] 1.3 Add the live survey row; verify that `live-cpm-props-survey-prompts-only-for-unscoped-reads` shows no `echo` candidate

## 2. F4: quote correction

- [x] 2.1 Add `GetUnboundedPathnameExpansionWords`, `ShellWordQuoteSuggested`, and its delivery; verify `Known_words_with_an_unquoted_expansion_get_a_quote_correction`
- [x] 2.2 Select the quote correction for a known-words candidate in `SelectCommandWordsCorrection`; verify the four quote rows and the two negative-control rows in the review catalog

## 3. Gates and docs

- [x] 3.1 Add the `IsScopeFreeDataCommand` mutation target and repoint the digest span; verify that the shell command analysis gate kills each mutant
- [x] 3.2 Update the `netclaw-operations` skill, the runbook, and the architecture doc; verify with `openspec validate`
- [x] 3.3 Run the authorization corpus against dev and list the outcome changes in the PR body
