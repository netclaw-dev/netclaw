| ID | Policy | Audience | Cwd | Interaction | Command | Approval state | Result | Reason |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| missing-policy-prompts | Missing/HostAllowed | Personal | Project | Interactive | git push origin dev | none | RequiresApproval | approval required |
| exact-approval-prompts | Approval/HostAllowed | Personal | Project | Interactive | git push origin dev | none | RequiresApproval | approval required |
| exact-auto-allows | Auto/HostAllowed | Personal | Project | Interactive | git push origin dev | none | Allowed | PolicyAuto |
| exact-deny-denies | Deny/HostAllowed | Personal | Project | Interactive | git push origin dev | none | Denied | tool_denied_by_approval_policy |
| missing-policy-persistent-grant-allows | Missing/HostAllowed | Personal | Project | Interactive | git push origin dev | persistent[anywhere]:git push origin dev | Allowed | StoredApproval |
| team-audience-denied | Approval/HostAllowed | Team | Project | Interactive | git push | none | Denied | shell_requires_personal_context |
| public-audience-denied | Approval/HostAllowed | Public | Project | Interactive | git push | none | Denied | shell_requires_personal_context |
| team-auto-still-denied | Auto/HostAllowed | Team | Project | Interactive | git push | none | Denied | shell_requires_personal_context |
| public-auto-still-denied | Auto/HostAllowed | Public | Project | Interactive | git push | none | Denied | shell_requires_personal_context |
| shell-off-denies | Auto/Off | Personal | Project | Interactive | git status | none | Denied | shell_disabled |
| sandbox-only-denies | Auto/SandboxOnly | Personal | Project | Interactive | git status | none | Denied | shell_requires_sandbox_backend |
| hard-deny-beats-approval | Approval/HostAllowed | Personal | Project | Interactive | netclaw daemon stop | none | Denied | hard_deny_self_destructive |
| hard-deny-beats-auto | Auto/HostAllowed | Personal | Project | Interactive | netclaw daemon stop | none | Denied | hard_deny_self_destructive |
| hard-deny-beats-stored-grant | Approval/HostAllowed | Personal | Project | Interactive | netclaw daemon stop | persistent[anywhere]:netclaw daemon stop | Denied | hard_deny_self_destructive |
| compound-hard-deny-denies | Auto/HostAllowed | Personal | Project | Interactive | git status && netclaw daemon stop | none | Denied | hard_deny_self_destructive |
| safe-verb-project-allows | Approval/HostAllowed | Personal | Project | Interactive | git status | none | Allowed | SafeVerbInTrustedScope |
| safe-verb-session-allows | Approval/HostAllowed | Personal | Session | Interactive | git status | none | Allowed | SafeVerbInTrustedScope |
| safe-verb-external-prompts | Approval/HostAllowed | Personal | External | Interactive | git status | none | RequiresApproval | approval required |
| safe-verb-external-path-prompts | Approval/HostAllowed | Personal | Project | Interactive | cat /etc/passwd | none | RequiresApproval | approval required |
| safe-verb-external-redirect-prompts | Approval/HostAllowed | Personal | Project | Interactive | git status > {TempPath}netclaw-approval-matrix.txt | none | RequiresApproval | approval required |
| mutating-verb-project-prompts | Approval/HostAllowed | Personal | Project | Interactive | git push | none | RequiresApproval | approval required |
| all-safe-compound-allows | Approval/HostAllowed | Personal | Project | Interactive | git status && git log | none | Allowed | SafeVerbInTrustedScope |
| mixed-safe-unsafe-compound-prompts | Approval/HostAllowed | Personal | Project | Interactive | git status && git push | none | RequiresApproval | approval required |
| safe-pipe-unsafe-tail-prompts | Approval/HostAllowed | Personal | Project | Interactive | git status \| git push | none | RequiresApproval | approval required |
| added-safe-verb-project-allows | Approval/HostAllowed+eza | Personal | Project | Interactive | eza | none | Allowed | SafeVerbInTrustedScope |
| echo-allows-without-grant | Approval/HostAllowed | Personal | Project | Interactive | echo hello | none | Allowed | ApprovalExemptShellCandidates |
| printf-allows-without-grant | Approval/HostAllowed | Personal | Project | Interactive | printf hello | none | Allowed | ApprovalExemptShellCandidates |
| echo-redirect-prompts | Approval/HostAllowed | Personal | Project | Interactive | echo hello > result.txt | none | RequiresApproval | approval required |
| echo-done-fails-closed | Approval/HostAllowed | Personal | Project | Interactive | echo done | none | RequiresApproval | approval required |
| control-flow-fails-closed | Approval/HostAllowed | Personal | Project | Interactive | for f in *.txt; do cat "$f"; done | persistent[anywhere]:cat | RequiresApproval | approval required |
| empty-command-fails-closed | Approval/HostAllowed | Personal | Project | Interactive |  | none | RequiresApproval | approval required |
| whitespace-command-fails-closed | Approval/HostAllowed | Personal | Project | Interactive |     | none | RequiresApproval | approval required |
| session-grant-allows | Approval/HostAllowed | Personal | Project | Interactive | git push | session[this-chat]:git push | Allowed | StoredApproval |
| other-session-grant-prompts | Approval/HostAllowed | Personal | Project | Interactive | git push | session[other-chat]:git push | RequiresApproval | approval required |
| persistent-anywhere-allows | Approval/HostAllowed | Personal | Project | Interactive | git push | persistent[anywhere]:git push | Allowed | StoredApproval |
| persistent-here-allows | Approval/HostAllowed | Personal | Project | Interactive | git push | persistent[project]:git push | Allowed | StoredApproval |
| persistent-here-directory-mismatch-prompts | Approval/HostAllowed | Personal | External | Interactive | git push | persistent[project]:git push | RequiresApproval | approval required |
| other-audience-grant-prompts | Approval/HostAllowed | Personal | Project | Interactive | git push | persistent[anywhere,Team]:git push | RequiresApproval | approval required |
| mixed-session-persistent-compound-allows | Approval/HostAllowed | Personal | Project | Interactive | git status && git push | session[this-chat]:git status, persistent[anywhere]:git push | Allowed | StoredApproval |
| partial-compound-grant-prompts | Approval/HostAllowed | Personal | Project | Interactive | git status && git push | persistent[anywhere]:git status | RequiresApproval | approval required |
| noninteractive-unapproved-requires-approval | Approval/HostAllowed | Personal | Project | Non-interactive | git push | none | RequiresApproval | approval required |
| noninteractive-persistent-grant-allows | Approval/HostAllowed | Personal | Project | Non-interactive | git push | persistent[anywhere]:git push | Allowed | StoredApproval |
| noninteractive-exempt-allows | Approval/HostAllowed | Personal | Project | Non-interactive | echo hello | none | Allowed | ApprovalExemptShellCandidates |
