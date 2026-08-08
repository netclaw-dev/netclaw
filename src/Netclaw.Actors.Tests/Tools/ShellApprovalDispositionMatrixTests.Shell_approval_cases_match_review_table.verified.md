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
| safe-verb-context-project-fallback-allows | Personal | None | Interactive | cat src/readme.txt | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-verb-context-project-traversal-prompts | Personal | None | Interactive | cat ../secret.txt | none | RequiresApproval | approval required | cat | No |
| safe-verb-session-allows | Personal | Session | Interactive | git status | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| safe-verb-external-prompts | Personal | External | Interactive | git status | none | RequiresApproval | approval required | git status | No |
| safe-verb-external-path-prompts | Personal | Project | Interactive | cat /etc/passwd | none | RequiresApproval | approval required | cat | No |
| safe-verb-quoted-external-path-prompts | Personal | Project | Interactive | cat "/etc/netclaw.secret" | none | RequiresApproval | approval required | cat | No |
| safe-verb-traversal-external-path-prompts | Personal | Project | Interactive | cat safe/../../../../../../etc/netclaw.secret | none | RequiresApproval | approval required | cat | No |
| safe-verb-namespaced-external-path-prompts | Personal | Project | Interactive | cat filesystem::/etc/netclaw.secret | none | RequiresApproval | approval required | cat | No |
| safe-verb-external-redirect-prompts | Personal | Project | Interactive | git status > {TempPath}netclaw-approval-matrix.txt | none | RequiresApproval | approval required | git status | No |
| mutating-verb-project-prompts | Personal | Project | Interactive | git push | none | RequiresApproval | approval required | git push | No |
| all-safe-compound-allows | Personal | Project | Interactive | git status && git log | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| four-safe-mixed-operator-clauses-allow | Personal | Project | Interactive | git status && git log \| head -20; pwd | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| mixed-safe-unsafe-compound-prompts | Personal | Project | Interactive | git status && git push | none | RequiresApproval | approval required | git status, git push | No |
| safe-pipe-unsafe-tail-prompts | Personal | Project | Interactive | git status \| git push | none | RequiresApproval | approval required | git status, git push | No |
| safe-pipeline-allows | Personal | Project | Interactive | git log \| head -20 | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| native-project-path-operand-allows-safe-verb | Personal | Project | Interactive | git diff install-skills.sh | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| native-external-path-operand-prompts | Personal | Project | Interactive | git diff /etc/passwd | none | RequiresApproval | approval required | git diff | No |
| native-project-path-operand-reuses-grant | Personal | Project | Interactive | kubectl apply deployment.yaml | persistent[project]:kubectl apply | Allowed | StoredApproval | none | Not applicable |
| native-external-path-operand-does-not-reuse-project-grant | Personal | Project | Interactive | kubectl apply /etc/deployment.yaml | persistent[project]:kubectl apply | RequiresApproval | approval required | kubectl apply | No |
| native-output-option-outside-scope-prompts | Personal | Project | Interactive | curl -D /etc/netclaw.headers https://example.invalid/api | persistent[project]:curl | RequiresApproval | approval required | curl | No |
| native-command-valued-option-fails-closed | Personal | Project | Interactive | tar --info-script=./helper.sh archive.tar | persistent[project]:tar | RequiresApproval | approval required | none | Yes |
| native-project-file-reference-reuses-grant | Personal | Project | Interactive | curl --data=@request.json https://example.invalid/api | persistent[project]:curl | Allowed | StoredApproval | none | Not applicable |
| native-external-file-reference-prompts | Personal | Project | Interactive | curl --data=@/etc/passwd https://example.invalid/api | persistent[project]:curl | RequiresApproval | approval required | curl | No |
| native-later-external-path-prompts | Personal | Project | Interactive | curl -D ./headers.txt --data=@/etc/passwd https://example.invalid/api | persistent[project]:curl | RequiresApproval | approval required | curl | No |
| native-earlier-external-path-prompts | Personal | Project | Interactive | curl -D /etc/netclaw.headers --data=@request.json https://example.invalid/api | persistent[project]:curl | RequiresApproval | approval required | curl | No |
| native-two-project-paths-reuse-grant | Personal | Project | Interactive | curl -D ./headers.txt --data=@request.json https://example.invalid/api | persistent[project]:curl | Allowed | StoredApproval | none | Not applicable |
| native-option-and-redirect-scopes-all-checked | Personal | Project | Interactive | curl --data=@/etc/passwd https://example.invalid/api > ./response.json | persistent[project]:curl | RequiresApproval | approval required | curl | No |
| native-dynamic-file-reference-fails-closed | Personal | Project | Interactive | curl --data=@$REQUEST_FILE https://example.invalid/api | persistent[project]:curl | RequiresApproval | approval required | none | Yes |
| local-glob-allows-safe-verb | Personal | Project | Interactive | ls *.txt | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| local-glob-reuses-project-grant | Personal | Project | Interactive | rm *.tmp | persistent[project]:rm | Allowed | StoredApproval | none | Not applicable |
| external-glob-does-not-reuse-project-grant | Personal | Project | Interactive | rm {TempPath}netclaw-ext-glob/*.bak | persistent[project]:rm | RequiresApproval | approval required | rm | No |
| glob-traversal-fails-closed | Personal | Project | Interactive | cat */../../secret.txt | persistent[anywhere]:cat | RequiresApproval | approval required | none | Yes |
| glob-intermediate-symlink-scope-fails-closed | Personal | Project | Interactive | cat artifacts/*/secret.txt | persistent[anywhere]:cat | RequiresApproval | approval required | none | Yes |
| directory-listing-glob-in-project-auto-allows | Personal | Project | Interactive | ls -d subdirs/*/ | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| directory-listing-glob-external-offers-persistent-grant | Personal | External | Interactive | ls -d subdirs/*/ | none | RequiresApproval | approval required | ls | No |
| directory-listing-glob-pipeline-offers-persistent-grant | Personal | External | Interactive | ls -d subdirs/*/ \| xargs -n1 basename | none | RequiresApproval | approval required | ls, xargs | No |
| native-global-option-identity-gap-currently-prompts | Personal | Project | Interactive | git --no-pager status | persistent[project]:git status | RequiresApproval | approval required | git | No |
| semicolon-sequence-prompts | Personal | Project | Interactive | git status; git push | none | RequiresApproval | approval required | git status, git push | No |
| newline-sequence-prompts | Personal | Project | Interactive | git status\ngit push | none | RequiresApproval | approval required | git status, git push | No |
| or-chain-prompts | Personal | Project | Interactive | git status \|\| git push | none | RequiresApproval | approval required | git status, git push | No |
| three-step-release-prompts | Personal | Project | Interactive | git add . && git commit -m fix && git push origin dev | none | RequiresApproval | approval required | git add, git commit, git push origin dev | No |
| hard-deny-pipeline-tail-blocks | Personal | Project | Interactive | echo safe \| netclaw daemon stop | none | Denied | hard_deny_self_destructive | none | Not applicable |
| hard-deny-nested-shell-blocks | Personal | Project | Interactive | bash -lc "netclaw daemon stop" | none | Denied | hard_deny_self_destructive | none | Not applicable |
| nested-shell-prompts-for-inner-command | Personal | Project | Interactive | bash -lc "git push" | none | RequiresApproval | approval required | git push | No |
| nested-shell-inner-grant-allows | Personal | Project | Interactive | bash -lc "git push" | persistent[anywhere]:git push | Allowed | StoredApproval | none | Not applicable |
| nested-shell-wrapper-grant-does-not-cover-inner-command | Personal | Project | Interactive | bash -lc "git push" | persistent[anywhere]:bash | RequiresApproval | approval required | git push | No |
| env-nested-shell-prompts | Personal | Project | Interactive | env bash -lc "git push" | none | RequiresApproval | approval required | git push | No |
| timeout-nested-shell-prompts | Personal | Project | Interactive | timeout 5 bash -lc "git push" | none | RequiresApproval | approval required | timeout, git push | No |
| subshell-prompts | Personal | Project | Interactive | (git status && git push) | none | RequiresApproval | approval required | git status, git push | No |
| command-substitution-fails-closed | Personal | Project | Interactive | echo $(git push) | none | RequiresApproval | approval required | none | Yes |
| dynamic-path-fails-closed | Personal | Project | Interactive | cat "$FILE" | none | RequiresApproval | approval required | none | Yes |
| dynamic-redirect-fails-closed | Personal | Project | Interactive | git status > "$OUTPUT" | none | RequiresApproval | approval required | none | Yes |
| fd-dup-redirect-safe-verb-allows | Personal | Project | Interactive | git status 2>&1 | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| fd-dup-redirect-safe-pipeline-allows | Personal | Project | Interactive | git log --oneline -5 2>&1 \| tail -20 | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| fd-close-redirect-safe-verb-allows | Personal | Project | Interactive | git status 2>&- | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| fd-move-redirect-safe-verb-allows | Personal | Project | Interactive | git status 2>&1- | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| fd-dup-redirect-mutating-no-grant-prompts-not-messy | Personal | Project | Interactive | git push origin dev 2>&1 \| tail -2 | none | RequiresApproval | approval required | git push origin dev, tail | No |
| dynamic-fd-redirect-fails-closed | Personal | Project | Interactive | git status 2>&$FD | none | RequiresApproval | approval required | none | Yes |
| background-list-prompts-for-mutating-tail | Personal | Project | Interactive | git status & git push | none | RequiresApproval | approval required | none | Yes |
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
| heredoc-prompts | Personal | Project | Interactive | cat <<'EOF'\nhello\nEOF | none | RequiresApproval | approval required | none | Yes |
| workload-search-rg-in-project-allows | Personal | Project | Interactive | rg -n "TODO" src | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-search-grep-in-project-allows | Personal | Project | Interactive | grep -R "error" src | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-search-find-in-project-allows | Personal | Project | Interactive | find src -name "*.cs" -print | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-search-cat-in-project-allows | Personal | Project | Interactive | cat src/file.txt | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-search-head-in-project-allows | Personal | Project | Interactive | head -40 src/file.txt | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-search-tail-in-project-allows | Personal | Project | Interactive | tail -100 logs/app.log | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-search-sed-print-in-project-currently-prompts | Personal | Project | Interactive | sed -n '20,80p' src/file.txt | none | RequiresApproval | approval required | sed | No |
| workload-search-rg-external-prompts | Personal | External | Interactive | rg -n "TODO" . | none | RequiresApproval | approval required | rg | No |
| workload-search-rg-external-grant-allows | Personal | External | Interactive | rg -n "TODO" . | persistent[external]:rg | Allowed | StoredApproval | none | Not applicable |
| workload-search-rg-head-pipeline-allows | Personal | Project | Interactive | rg -n "TODO" src \| head -40 | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-search-grep-tail-pipeline-allows | Personal | Project | Interactive | grep -R "error" logs \| tail -20 | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-search-find-head-pipeline-allows | Personal | Project | Interactive | find src -name "*.cs" -print \| head -20 | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-search-cat-jq-pipeline-currently-prompts | Personal | Project | Interactive | cat config.json \| jq '.items[]' | none | RequiresApproval | approval required | cat, jq | No |
| workload-search-jq-direct-prompts | Personal | Project | Interactive | jq '.items[]' config.json | none | RequiresApproval | approval required | jq | No |
| workload-search-jq-direct-grant-allows | Personal | Project | Interactive | jq '.items[]' config.json | persistent[project]:jq | Allowed | StoredApproval | none | Not applicable |
| workload-search-cat-jq-stored-tail-currently-prompts | Personal | Project | Interactive | cat config.json \| jq '.items[]' | persistent[project]:jq | RequiresApproval | approval required | cat, jq | No |
| workload-edit-grep-tee-pipeline-prompts | Personal | Project | Interactive | grep "error" logs/app.log \| tee reports/errors.txt | none | RequiresApproval | approval required | grep, tee | No |
| workload-edit-tee-direct-prompts | Personal | Project | Interactive | tee reports/output.txt | none | RequiresApproval | approval required | tee | No |
| workload-edit-tee-direct-grant-allows | Personal | Project | Interactive | tee reports/output.txt | persistent[project]:tee | Allowed | StoredApproval | none | Not applicable |
| workload-edit-grep-tee-stored-tail-currently-prompts | Personal | Project | Interactive | grep "error" logs/app.log \| tee reports/errors.txt | persistent[project]:tee | RequiresApproval | approval required | grep, tee | No |
| workload-edit-sed-in-place-prompts | Personal | Project | Interactive | sed -i 's/old/new/' src/file.txt | none | RequiresApproval | approval required | sed | No |
| workload-edit-sed-in-place-grant-allows | Personal | Project | Interactive | sed -i 's/old/new/' src/file.txt | persistent[project]:sed | Allowed | StoredApproval | none | Not applicable |
| workload-edit-copy-prompts | Personal | Project | Interactive | cp src/input.txt src/output.txt | none | RequiresApproval | approval required | cp | No |
| workload-edit-copy-grant-allows | Personal | Project | Interactive | cp src/input.txt src/output.txt | persistent[project]:cp | Allowed | StoredApproval | none | Not applicable |
| workload-edit-move-prompts | Personal | Project | Interactive | mv src/old.txt src/new.txt | none | RequiresApproval | approval required | mv | No |
| workload-edit-move-grant-allows | Personal | Project | Interactive | mv src/old.txt src/new.txt | persistent[project]:mv | Allowed | StoredApproval | none | Not applicable |
| workload-edit-touch-prompts | Personal | Project | Interactive | touch src/new.txt | none | RequiresApproval | approval required | touch | No |
| workload-edit-touch-grant-allows | Personal | Project | Interactive | touch src/new.txt | persistent[project]:touch | Allowed | StoredApproval | none | Not applicable |
| workload-edit-mkdir-prompts | Personal | Project | Interactive | mkdir -p reports/output | none | RequiresApproval | approval required | mkdir | No |
| workload-edit-mkdir-grant-allows | Personal | Project | Interactive | mkdir -p reports/output | persistent[project]:mkdir | Allowed | StoredApproval | none | Not applicable |
| workload-edit-remove-prompts | Personal | Project | Interactive | rm -- src/obsolete.txt | none | RequiresApproval | approval required | rm | No |
| workload-edit-remove-grant-allows | Personal | Project | Interactive | rm -- src/obsolete.txt | persistent[project]:rm | Allowed | StoredApproval | none | Not applicable |
| workload-edit-printf-redirect-prompts | Personal | Project | Interactive | printf '%s\n' "text" > reports/output.txt | none | RequiresApproval | approval required | printf | No |
| workload-edit-printf-redirect-grant-allows | Personal | Project | Interactive | printf '%s\n' "text" > reports/output.txt | persistent[project]:printf | Allowed | StoredApproval | none | Not applicable |
| workload-edit-search-pipeline-redirect-in-project-allows | Personal | Project | Interactive | grep -R "error" logs \| head -20 > reports/errors.txt | none | Allowed | SafeVerbInTrustedScope | none | Not applicable |
| workload-edit-search-pipeline-redirect-external-prompts | Personal | External | Interactive | grep -R "error" logs \| head -20 > reports/errors.txt | none | RequiresApproval | approval required | grep, head | No |
| workload-edit-search-pipeline-redirect-external-grant-allows | Personal | External | Interactive | grep -R "error" logs \| head -20 > reports/errors.txt | persistent[external]:grep, persistent[external]:head | Allowed | StoredApproval | none | Not applicable |
| workload-search-loop-currently-complex | Personal | Project | Interactive | for f in src/*.cs; do grep -n "TODO" "$f"; done | persistent[project]:grep | RequiresApproval | approval required | none | Yes |
| workload-edit-loop-currently-complex | Personal | Project | Interactive | for f in src/a.txt src/b.txt; do sed -i 's/old/new/' "$f"; done | persistent[project]:sed | RequiresApproval | approval required | none | Yes |
| workload-search-dynamic-root-remains-complex | Personal | Project | Interactive | grep -R "error" "$SEARCH_ROOT" | persistent[anywhere]:grep | RequiresApproval | approval required | none | Yes |
| workload-search-substitution-pipeline-redirect-remains-complex | Personal | Project | Interactive | pattern=$(printf '%s' error); grep -R "$pattern" src \| head -20 > reports/errors.txt | persistent[project]:grep, persistent[project]:head, persistent[project]:printf | RequiresApproval | approval required | none | Yes |
| workload-search-loop-substitution-pipeline-redirect-remains-complex | Personal | Project | Interactive | for f in logs/*.log; do grep -n "$(printf '%s' error)" "$f" \| head -20 > "reports/$f.txt"; done | persistent[project]:grep, persistent[project]:head, persistent[project]:printf | RequiresApproval | approval required | none | Yes |
| echo-allows-without-grant | Personal | Project | Interactive | echo hello | none | Allowed | ApprovalExemptShellCandidates | none | Not applicable |
| printf-allows-without-grant | Personal | Project | Interactive | printf hello | none | Allowed | ApprovalExemptShellCandidates | none | Not applicable |
| echo-redirect-prompts | Personal | Project | Interactive | echo hello > result.txt | none | RequiresApproval | approval required | echo | No |
| echo-control-word-argument-allows | Personal | Project | Interactive | echo done | none | Allowed | ApprovalExemptShellCandidates | none | Not applicable |
| control-flow-fails-closed | Personal | Project | Interactive | for f in *.txt; do cat "$f"; done | persistent[anywhere]:cat | RequiresApproval | approval required | none | Yes |
| process-substitution-fails-closed | Personal | Project | Interactive | cat <(git push) | persistent[anywhere]:cat, persistent[anywhere]:git push | RequiresApproval | approval required | none | Yes |
| arithmetic-expansion-fails-closed | Personal | Project | Interactive | echo $((1 + 2)) | none | RequiresApproval | approval required | none | Yes |
| function-definition-fails-closed | Personal | Project | Interactive | deploy() { git push; }; deploy | persistent[anywhere]:git push | RequiresApproval | approval required | none | Yes |
| inline-python-prompts-for-interpreter | Personal | Project | Interactive | python3 -c "print('hello')" | none | RequiresApproval | approval required | python3 | No |
| inline-python-interpreter-grant-currently-allows | Personal | Project | Interactive | python3 -c "print('hello')" | persistent[anywhere]:python3 | Allowed | StoredApproval | none | Not applicable |
| eval-prompts-for-interpreter | Personal | Project | Interactive | eval "$CODE" | none | RequiresApproval | approval required | eval | No |
| eval-grant-currently-allows-dynamic-payload | Personal | Project | Interactive | eval "$CODE" | persistent[anywhere]:eval | Allowed | StoredApproval | none | Not applicable |
| inline-python-heredoc-fails-closed | Personal | Project | Interactive | python3 <<'PY'\nprint('hello')\nPY | persistent[anywhere]:python3 | RequiresApproval | approval required | none | Yes |
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
| four-unapproved-clauses-prompt | Personal | Project | Interactive | git add . && git commit -m fix && git push && gh pr merge 123 | none | RequiresApproval | approval required | git add, git commit, git push, gh pr merge | No |
| four-anywhere-grants-allow | Personal | Project | Interactive | git add . && git commit -m fix && git push && gh pr merge 123 | persistent[anywhere]:git add, persistent[anywhere]:git commit, persistent[anywhere]:git push, persistent[anywhere]:gh pr merge | Allowed | StoredApproval | none | Not applicable |
| four-one-missing-grant-prompts | Personal | Project | Interactive | git add . && git commit -m fix && git push && gh pr merge 123 | persistent[anywhere]:git add, persistent[anywhere]:git commit, persistent[anywhere]:git push | RequiresApproval | approval required | git add, git commit, git push, gh pr merge | No |
| four-here-grants-allow | Personal | Project | Interactive | git add . && git commit -m fix && git push && gh pr merge 123 | persistent[project]:git add, persistent[project]:git commit, persistent[project]:git push, persistent[project]:gh pr merge | Allowed | StoredApproval | none | Not applicable |
| four-one-wrong-directory-grant-prompts | Personal | Project | Interactive | git add . && git commit -m fix && git push && gh pr merge 123 | persistent[project]:git add, persistent[project]:git commit, persistent[project]:git push, persistent[external]:gh pr merge | RequiresApproval | approval required | git add, git commit, git push, gh pr merge | No |
| four-one-other-session-grant-prompts | Personal | Project | Interactive | git add . && git commit -m fix && git push && gh pr merge 123 | session[this-chat]:git add, session[this-chat]:git commit, session[this-chat]:git push, session[other-chat]:gh pr merge | RequiresApproval | approval required | git add, git commit, git push, gh pr merge | No |
| four-one-other-audience-grant-prompts | Personal | Project | Interactive | git add . && git commit -m fix && git push && gh pr merge 123 | persistent[anywhere]:git add, persistent[anywhere]:git commit, persistent[anywhere]:git push, persistent[anywhere,Team]:gh pr merge | RequiresApproval | approval required | git add, git commit, git push, gh pr merge | No |
| four-mixed-grant-sources-allow | Personal | Project | Interactive | git add . && git commit -m fix && git push && gh pr merge 123 | session[this-chat]:git add, session[this-chat]:gh pr merge, persistent[project]:git commit, persistent[anywhere]:git push | Allowed | StoredApproval | none | Not applicable |
| safe-and-stored-authority-currently-do-not-compose | Personal | Project | Interactive | git status && git push && git log && gh pr merge 123 | persistent[anywhere]:git push, persistent[anywhere]:gh pr merge | RequiresApproval | approval required | git status, git push, git log, gh pr merge | No |
| four-hard-deny-beats-grants | Personal | Project | Interactive | git add . && git commit -m fix && netclaw daemon stop && git push | persistent[anywhere]:git add, persistent[anywhere]:git commit, persistent[anywhere]:netclaw daemon stop, persistent[anywhere]:git push | Denied | hard_deny_self_destructive | none | Not applicable |
| four-or-branches-with-grants-allow | Personal | Project | Interactive | git add . \|\| git commit -m fix \|\| git push \|\| gh pr merge 123 | persistent[anywhere]:git add, persistent[anywhere]:git commit, persistent[anywhere]:git push, persistent[anywhere]:gh pr merge | Allowed | StoredApproval | none | Not applicable |
| four-newline-statements-with-grants-allow | Personal | Project | Interactive | git add .\ngit commit -m fix\ngit push\ngh pr merge 123 | persistent[anywhere]:git add, persistent[anywhere]:git commit, persistent[anywhere]:git push, persistent[anywhere]:gh pr merge | Allowed | StoredApproval | none | Not applicable |
| four-subshell-clauses-with-grants-allow | Personal | Project | Interactive | (git add . && git commit -m fix) \|\| (git push && gh pr merge 123) | persistent[anywhere]:git add, persistent[anywhere]:git commit, persistent[anywhere]:git push, persistent[anywhere]:gh pr merge | Allowed | StoredApproval | none | Not applicable |
| noninteractive-unapproved-requires-approval | Personal | Project | Non-interactive | git push | none | RequiresApproval | approval required | git push | No |
| noninteractive-persistent-grant-allows | Personal | Project | Non-interactive | git push | persistent[anywhere]:git push | Allowed | StoredApproval | none | Not applicable |
| noninteractive-exempt-allows | Personal | Project | Non-interactive | echo hello | none | Allowed | ApprovalExemptShellCandidates | none | Not applicable |
