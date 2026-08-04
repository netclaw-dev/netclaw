# Fresh Personal approval matrix

`Tools.ShellMode`: `HostAllowed`

`Personal.ApprovalPolicy.shell_execute`: `Approval`

| ID | Audience | Cwd | Interaction | Command | Approval state | Result | Reason | Candidates | Complex |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| mutating-command-prompts | Personal | Project | Interactive | git push origin dev | none | RequiresApproval | approval required | git push origin dev | No |
| team-audience-denied | Team | Project | Interactive | git push | none | Denied | tool_not_allowed_for_audience_profile | none | Not applicable |
| public-audience-denied | Public | Project | Interactive | git push | none | Denied | tool_not_allowed_for_audience_profile | none | Not applicable |
| hard-deny-blocks | Personal | Project | Interactive | netclaw daemon stop | none | Denied | hard_deny_self_destructive | none | Not applicable |
| hard-deny-beats-stored-grant | Personal | Project | Interactive | netclaw daemon stop | persistent[anywhere]:netclaw daemon stop | Denied | hard_deny_self_destructive | none | Not applicable |
| compound-hard-deny-denies | Personal | Project | Interactive | git status && netclaw daemon stop | none | Denied | hard_deny_self_destructive | none | Not applicable |
| safe-verb-project-allows | Personal | Project | Interactive | git status | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-verb-session-allows | Personal | Session | Interactive | git status | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-verb-external-prompts | Personal | External | Interactive | git status | none | RequiresApproval | approval required | git status | No |
| safe-verb-external-path-prompts | Personal | Project | Interactive | cat /etc/passwd | none | RequiresApproval | approval required | cat | No |
| safe-verb-external-redirect-prompts | Personal | Project | Interactive | git status > {TempPath}netclaw-approval-matrix.txt | none | RequiresApproval | approval required | git status | No |
| mutating-verb-project-prompts | Personal | Project | Interactive | git push | none | RequiresApproval | approval required | git push | No |
| all-safe-compound-allows | Personal | Project | Interactive | git status && git log | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| mixed-safe-unsafe-compound-prompts | Personal | Project | Interactive | git status && git push | none | RequiresApproval | approval required | git status, git push | No |
| safe-pipe-unsafe-tail-prompts | Personal | Project | Interactive | git status \| git push | none | RequiresApproval | approval required | git status, git push | No |
| safe-pipeline-allows | Personal | Project | Interactive | git log \| head -20 | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| semicolon-sequence-prompts | Personal | Project | Interactive | git status; git push | none | RequiresApproval | approval required | git status, git push | No |
| newline-sequence-prompts | Personal | Project | Interactive | git status\ngit push | none | RequiresApproval | approval required | git status, git push | No |
| or-chain-prompts | Personal | Project | Interactive | git status \|\| git push | none | RequiresApproval | approval required | git status, git push | No |
| three-step-release-prompts | Personal | Project | Interactive | git add . && git commit -m fix && git push origin dev | none | RequiresApproval | approval required | git add, git commit, git push origin dev | No |
| hard-deny-pipeline-tail-currently-prompts | Personal | Project | Interactive | echo safe \| netclaw daemon stop | none | RequiresApproval | approval required | echo, netclaw daemon stop | No |
| hard-deny-nested-shell-blocks | Personal | Project | Interactive | bash -lc "netclaw daemon stop" | none | Denied | hard_deny_self_destructive | none | Not applicable |
| nested-shell-currently-prompts-for-wrapper | Personal | Project | Interactive | bash -lc "git push" | none | RequiresApproval | approval required | bash | No |
| nested-shell-inner-grant-currently-does-not-match | Personal | Project | Interactive | bash -lc "git push" | persistent[anywhere]:git push | RequiresApproval | approval required | bash | No |
| nested-shell-wrapper-grant-currently-allows | Personal | Project | Interactive | bash -lc "git push" | persistent[anywhere]:bash | Allowed | StoredApproval | none | Not applicable |
| env-nested-shell-prompts | Personal | Project | Interactive | env bash -lc "git push" | none | RequiresApproval | approval required | env bash | No |
| timeout-nested-shell-prompts | Personal | Project | Interactive | timeout 5 bash -lc "git push" | none | RequiresApproval | approval required | timeout | No |
| subshell-prompts | Personal | Project | Interactive | (git status && git push) | none | RequiresApproval | approval required | git status, git push | No |
| command-substitution-currently-auto-allows | Personal | Project | Interactive | echo $(git push) | none | Allowed | ApprovalExemptShellCandidates | none | Not applicable |
| background-list-currently-auto-allows | Personal | Project | Interactive | git status & git push | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| unbalanced-quote-fails-closed | Personal | Project | Interactive | git push "unterminated | none | RequiresApproval | approval required | none | Yes |
| multiline-argument-prompts | Personal | Project | Interactive | gh issue comment 123 --body "first line\nsecond line" | none | RequiresApproval | approval required | gh issue comment | No |
| approved-pipeline-head-does-not-cover-tail | Personal | Project | Interactive | git push \| curl https://example.com | persistent[anywhere]:git push | RequiresApproval | approval required | git push, curl | No |
| all-pipeline-clauses-approved | Personal | Project | Interactive | git push \| curl https://example.com | persistent[anywhere]:git push, persistent[anywhere]:curl | Allowed | StoredApproval | none | Not applicable |
| input-redirect-outside-zone-prompts | Personal | Project | Interactive | cat < /etc/passwd | none | RequiresApproval | approval required | cat | No |
| error-redirect-outside-zone-prompts | Personal | Project | Interactive | git status 2> {TempPath}netclaw-approval-errors.txt | none | RequiresApproval | approval required | git status | No |
| cd-current-then-safe-allows | Personal | Project | Interactive | cd . && git status | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| cd-parent-then-safe-prompts | Personal | Project | Interactive | cd .. && git status | none | RequiresApproval | approval required | cd, git status | No |
| multiple-cd-then-safe-prompts | Personal | Project | Interactive | cd . && cd .. && git status | none | RequiresApproval | approval required | cd, git status | No |
| side-effect-before-mutation-prompts | Personal | Project | Interactive | echo ready && git push | none | RequiresApproval | approval required | echo, git push | No |
| heredoc-prompts | Personal | Project | Interactive | cat <<'EOF'\nhello\nEOF | none | RequiresApproval | approval required | none | No |
| echo-allows-without-grant | Personal | Project | Interactive | echo hello | none | Allowed | ApprovalExemptShellCandidates | none | Not applicable |
| printf-allows-without-grant | Personal | Project | Interactive | printf hello | none | Allowed | ApprovalExemptShellCandidates | none | Not applicable |
| echo-redirect-prompts | Personal | Project | Interactive | echo hello > result.txt | none | RequiresApproval | approval required | echo | No |
| echo-done-fails-closed | Personal | Project | Interactive | echo done | none | RequiresApproval | approval required | echo | Yes |
| control-flow-fails-closed | Personal | Project | Interactive | for f in *.txt; do cat "$f"; done | persistent[anywhere]:cat | RequiresApproval | approval required | none | Yes |
| empty-command-fails-closed | Personal | Project | Interactive |  | none | RequiresApproval | approval required | none | No |
| whitespace-command-fails-closed | Personal | Project | Interactive |     | none | RequiresApproval | approval required | none | No |
| session-grant-allows | Personal | Project | Interactive | git push | session[this-chat]:git push | Allowed | StoredApproval | none | Not applicable |
| other-session-grant-prompts | Personal | Project | Interactive | git push | session[other-chat]:git push | RequiresApproval | approval required | git push | No |
| persistent-anywhere-allows | Personal | Project | Interactive | git push | persistent[anywhere]:git push | Allowed | StoredApproval | none | Not applicable |
| persistent-here-allows | Personal | Project | Interactive | git push | persistent[project]:git push | Allowed | StoredApproval | none | Not applicable |
| persistent-here-directory-mismatch-prompts | Personal | External | Interactive | git push | persistent[project]:git push | RequiresApproval | approval required | git push | No |
| other-audience-grant-prompts | Personal | Project | Interactive | git push | persistent[anywhere,Team]:git push | RequiresApproval | approval required | git push | No |
| mixed-session-persistent-compound-allows | Personal | Project | Interactive | git status && git push | session[this-chat]:git status, persistent[anywhere]:git push | Allowed | StoredApproval | none | Not applicable |
| partial-compound-grant-prompts | Personal | Project | Interactive | git status && git push | persistent[anywhere]:git status | RequiresApproval | approval required | git status, git push | No |
| noninteractive-unapproved-requires-approval | Personal | Project | Non-interactive | git push | none | RequiresApproval | approval required | git push | No |
| noninteractive-persistent-grant-allows | Personal | Project | Non-interactive | git push | persistent[anywhere]:git push | Allowed | StoredApproval | none | Not applicable |
| noninteractive-exempt-allows | Personal | Project | Non-interactive | echo hello | none | Allowed | ApprovalExemptShellCandidates | none | Not applicable |
