# Fresh Personal PowerShell approval matrix

`Tools.ShellMode`: `HostAllowed`

`Personal.ApprovalPolicy.shell_execute`: `Approval`

| ID | Audience | Cwd | Interaction | Command | Approval state | Result | Reason | Candidates | Complex |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| mutating-command-prompts | Personal | Project | Interactive | Set-Content -Path notes.txt -Value hello | none | RequiresApproval | approval required | Set-Content | No |
| team-audience-denied | Team | Project | Interactive | Set-Content notes.txt hello | none | Denied | tool_not_allowed_for_audience_profile | none | Not applicable |
| public-audience-denied | Public | Project | Interactive | Set-Content notes.txt hello | none | Denied | tool_not_allowed_for_audience_profile | none | Not applicable |
| hard-deny-blocks | Personal | Project | Interactive | Stop-Process -Id 1 | none | Denied | hard_deny_self_destructive | none | Not applicable |
| hard-deny-alias-blocks | Personal | Project | Interactive | spps -Id 1 | none | Denied | hard_deny_self_destructive | none | Not applicable |
| hard-deny-beats-stored-grant | Personal | Project | Interactive | Stop-Process -Id 1 | persistent[anywhere]:Stop-Process | Denied | hard_deny_self_destructive | none | Not applicable |
| hard-deny-pipeline-tail-blocks | Personal | Project | Interactive | Get-Date \| Stop-Process -Id 1 | none | Denied | hard_deny_self_destructive | none | Not applicable |
| unsupported-cmd-wrapper-blocks | Personal | Project | Interactive | cmd.exe /c whoami | none | Denied | hard_deny_custom_deny | none | Not applicable |
| unsupported-windows-powershell-wrapper-blocks | Personal | Project | Interactive | powershell.exe -Command Get-Date | none | Denied | hard_deny_custom_deny | none | Not applicable |
| encoded-hard-deny-blocks | Personal | Project | Interactive | pwsh -EncodedCommand cwBwAHAAcwAgAC0ASQBkACAAMQA= | none | Denied | hard_deny_self_destructive | none | Not applicable |
| safe-cmdlet-project-allows | Personal | Project | Interactive | Get-ChildItem | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-alias-project-allows | Personal | Project | Interactive | gci | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-cmdlet-session-allows | Personal | Session | Interactive | Get-ChildItem | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-cmdlet-external-prompts | Personal | External | Interactive | Get-ChildItem | none | RequiresApproval | approval required | Get-ChildItem | No |
| safe-project-path-allows | Personal | Project | Interactive | Get-Content .\notes.txt | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-external-path-prompts | Personal | Project | Interactive | Get-Content C:\Windows\win.ini | none | RequiresApproval | approval required | Get-Content | No |
| environment-provider-prompts | Personal | Project | Interactive | Get-ChildItem Env: | none | RequiresApproval | approval required | Get-ChildItem | No |
| registry-provider-prompts | Personal | Project | Interactive | Get-ChildItem Registry::HKEY_LOCAL_MACHINE\SOFTWARE | none | RequiresApproval | approval required | Get-ChildItem | No |
| provider-grant-allows | Personal | Project | Interactive | Get-ChildItem Env: | persistent[anywhere]:Get-ChildItem | Allowed | StoredApproval | none | Not applicable |
| safe-pipeline-allows | Personal | Project | Interactive | Get-ChildItem \| Select-Object -First 5 | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| four-safe-pipeline-clauses-allow | Personal | Project | Interactive | Get-ChildItem \| Select-Object -First 5 \| Sort-Object Name \| Format-Table Name | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-pipeline-mutating-tail-prompts | Personal | Project | Interactive | Get-ChildItem \| Set-Content result.txt | none | RequiresApproval | approval required | Get-ChildItem, Set-Content | No |
| semicolon-sequence-prompts | Personal | Project | Interactive | Get-Date; Set-Content notes.txt hello | none | RequiresApproval | approval required | Get-Date, Set-Content | No |
| newline-sequence-prompts | Personal | Project | Interactive | Get-Date\nSet-Content notes.txt hello | none | RequiresApproval | approval required | Get-Date, Set-Content | No |
| where-object-remains-gated | Personal | Project | Interactive | Get-ChildItem \| Where-Object Name -Like *.txt | none | RequiresApproval | approval required | Get-ChildItem, Where-Object | No |
| mutating-alias-prompts-for-canonical-cmdlet | Personal | Project | Interactive | ri notes.txt | none | RequiresApproval | approval required | Remove-Item | No |
| canonical-grant-allows-alias | Personal | Project | Interactive | ri notes.txt | persistent[anywhere]:Remove-Item | Allowed | StoredApproval | none | Not applicable |
| alias-grant-does-not-match-canonical-cmdlet | Personal | Project | Interactive | ri notes.txt | persistent[anywhere]:ri | RequiresApproval | approval required | Remove-Item | No |
| stored-grant-matches-with-different-case | Personal | Project | Interactive | Set-Content notes.txt hello | persistent[anywhere]:set-content | Allowed | StoredApproval | none | Not applicable |
| module-qualified-command-uses-canonical-cmdlet | Personal | Project | Interactive | Microsoft.PowerShell.Management\Set-Content notes.txt hello | persistent[anywhere]:Set-Content | Allowed | StoredApproval | none | Not applicable |
| invoke-expression-prompts | Personal | Project | Interactive | Invoke-Expression $code | none | RequiresApproval | approval required | Invoke-Expression | No |
| invoke-expression-grant-currently-allows-dynamic-payload | Personal | Project | Interactive | Invoke-Expression $code | persistent[anywhere]:Invoke-Expression | Allowed | StoredApproval | none | Not applicable |
| iex-alias-uses-invoke-expression-approval | Personal | Project | Interactive | iex $code | persistent[anywhere]:Invoke-Expression | Allowed | StoredApproval | none | Not applicable |
| start-process-prompts | Personal | Project | Interactive | Start-Process notepad.exe | none | RequiresApproval | approval required | Start-Process | No |
| dynamic-invocation-fails-closed | Personal | Project | Interactive | & $command --version | none | RequiresApproval | approval required | none | Yes |
| dynamic-path-fails-closed | Personal | Project | Interactive | Get-Content $env:TEMP\secret.txt | persistent[anywhere]:Get-Content | RequiresApproval | approval required | none | Yes |
| script-block-fails-closed | Personal | Project | Interactive | Get-ChildItem \| Sort-Object { Remove-Item $_ } | persistent[anywhere]:Get-ChildItem, persistent[anywhere]:Sort-Object, persistent[anywhere]:Remove-Item | RequiresApproval | approval required | none | Yes |
| calculated-property-fails-closed | Personal | Project | Interactive | Get-ChildItem \| Select-Object @{N='x';E={Remove-Item $_}} | persistent[anywhere]:Get-ChildItem, persistent[anywhere]:Select-Object, persistent[anywhere]:Remove-Item | RequiresApproval | approval required | none | Yes |
| splatting-fails-closed | Personal | Project | Interactive | Get-ChildItem @args | persistent[anywhere]:Get-ChildItem | RequiresApproval | approval required | none | Yes |
| subexpression-fails-closed | Personal | Project | Interactive | Get-ChildItem $(Get-Content secret.txt) | persistent[anywhere]:Get-ChildItem, persistent[anywhere]:Get-Content | RequiresApproval | approval required | none | Yes |
| array-expression-fails-closed | Personal | Project | Interactive | Get-ChildItem @(Get-Content secret.txt) | persistent[anywhere]:Get-ChildItem, persistent[anywhere]:Get-Content | RequiresApproval | approval required | none | Yes |
| foreach-script-block-fails-closed | Personal | Project | Interactive | Get-ChildItem \| ForEach-Object { Remove-Item $_ } | persistent[anywhere]:Get-ChildItem, persistent[anywhere]:ForEach-Object, persistent[anywhere]:Remove-Item | RequiresApproval | approval required | none | Yes |
| unbalanced-quote-fails-closed | Personal | Project | Interactive | Set-Content notes.txt "unterminated | none | RequiresApproval | approval required | none | Yes |
| empty-command-fails-closed | Personal | Project | Interactive |  | none | RequiresApproval | approval required | none | No |
| write-output-prompts | Personal | Project | Interactive | Write-Output hello | none | RequiresApproval | approval required | Write-Output | No |
| echo-alias-prompts-for-write-output | Personal | Project | Interactive | echo hello | none | RequiresApproval | approval required | Write-Output | No |
| get-command-prompts | Personal | Project | Interactive | Get-Command git | none | RequiresApproval | approval required | Get-Command | No |
| get-help-prompts | Personal | Project | Interactive | Get-Help Get-ChildItem | none | RequiresApproval | approval required | Get-Help | No |
| get-process-prompts | Personal | Project | Interactive | Get-Process | none | RequiresApproval | approval required | Get-Process | No |
| safe-redirect-inside-project-allows | Personal | Project | Interactive | Get-Date > result.txt | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-redirect-outside-project-prompts | Personal | Project | Interactive | Get-Date > C:\Windows\Temp\netclaw-approval.txt | none | RequiresApproval | approval required | Get-Date | No |
| session-grant-allows | Personal | Project | Interactive | Set-Content notes.txt hello | session[this-chat]:Set-Content | Allowed | StoredApproval | none | Not applicable |
| other-session-grant-prompts | Personal | Project | Interactive | Set-Content notes.txt hello | session[other-chat]:Set-Content | RequiresApproval | approval required | Set-Content | No |
| persistent-anywhere-allows | Personal | Project | Interactive | Set-Content notes.txt hello | persistent[anywhere]:Set-Content | Allowed | StoredApproval | none | Not applicable |
| persistent-here-allows | Personal | Project | Interactive | Set-Content notes.txt hello | persistent[project]:Set-Content | Allowed | StoredApproval | none | Not applicable |
| persistent-here-directory-mismatch-prompts | Personal | External | Interactive | Set-Content notes.txt hello | persistent[project]:Set-Content | RequiresApproval | approval required | Set-Content | No |
| other-audience-grant-prompts | Personal | Project | Interactive | Set-Content notes.txt hello | persistent[anywhere,Team]:Set-Content | RequiresApproval | approval required | Set-Content | No |
| mixed-session-persistent-sequence-allows | Personal | Project | Interactive | Set-Content a.txt one; Remove-Item b.txt | session[this-chat]:Set-Content, persistent[anywhere]:Remove-Item | Allowed | StoredApproval | none | Not applicable |
| partial-sequence-grant-prompts | Personal | Project | Interactive | Set-Content a.txt one; Remove-Item b.txt | persistent[anywhere]:Set-Content | RequiresApproval | approval required | Set-Content, Remove-Item | No |
| safe-and-stored-authority-do-not-compose | Personal | Project | Interactive | Get-Date; Set-Content notes.txt hello | persistent[anywhere]:Set-Content | RequiresApproval | approval required | Get-Date, Set-Content | No |
| four-unapproved-statements-prompt | Personal | Project | Interactive | Set-Content a.txt one; Copy-Item a.txt b.txt; Move-Item b.txt c.txt; Remove-Item c.txt | none | RequiresApproval | approval required | Set-Content, Copy-Item, Move-Item, Remove-Item | No |
| four-anywhere-grants-allow | Personal | Project | Interactive | Set-Content a.txt one; Copy-Item a.txt b.txt; Move-Item b.txt c.txt; Remove-Item c.txt | persistent[anywhere]:Set-Content, persistent[anywhere]:Copy-Item, persistent[anywhere]:Move-Item, persistent[anywhere]:Remove-Item | Allowed | StoredApproval | none | Not applicable |
| four-one-missing-grant-prompts | Personal | Project | Interactive | Set-Content a.txt one; Copy-Item a.txt b.txt; Move-Item b.txt c.txt; Remove-Item c.txt | persistent[anywhere]:Set-Content, persistent[anywhere]:Copy-Item, persistent[anywhere]:Move-Item | RequiresApproval | approval required | Set-Content, Copy-Item, Move-Item, Remove-Item | No |
| four-hard-deny-beats-grants | Personal | Project | Interactive | Set-Content a.txt one; Copy-Item a.txt b.txt; Stop-Process -Id 1; Remove-Item b.txt | persistent[anywhere]:Set-Content, persistent[anywhere]:Copy-Item, persistent[anywhere]:Stop-Process, persistent[anywhere]:Remove-Item | Denied | hard_deny_self_destructive | none | Not applicable |
| noninteractive-unapproved-requires-approval | Personal | Project | Non-interactive | Set-Content notes.txt hello | none | RequiresApproval | approval required | Set-Content | No |
| noninteractive-persistent-grant-allows | Personal | Project | Non-interactive | Set-Content notes.txt hello | persistent[anywhere]:Set-Content | Allowed | StoredApproval | none | Not applicable |
