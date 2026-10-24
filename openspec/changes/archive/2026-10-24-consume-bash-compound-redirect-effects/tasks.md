## 1. Parser facts

- [x] 1.1 Pin the reviewed ShellSyntaxTree API and enable compound redirect parsing only in Netclaw's Bash consumer; verify default-off behavior and the unchanged PowerShell parse.
- [x] 1.2 Retain each parsed source and syntax root in `ShellCommandAnalysis`; test owner and child association by parser node identity.
- [x] 1.3 Validate exact consent candidates against this call's retained roots; reject foreign owners, foreign redirects, wrong source text, and wrong file-operation identity.
- [x] 1.4 Keep each outer redirect separate from child occurrences; test accurate `IsComplete`, owner cwd, redirect order, and source spans.

## 2. Path and prohibition checks

- [x] 2.1 Project each owner into call-level redirect path views; test exact targets against the owner's entry cwd and deny unknown or incomplete effects.
- [x] 2.2 Pass compound redirect views through hard-deny, protected-path, trusted-root, link, and file read/write checks; test a denied target before grant lookup.
- [x] 2.3 Test nested owners, multiple redirects, unknown owner cwd, unsupported redirects, and the current hard-deny visibility for rejected substitutions. Focused tests cover these parser and path cases. Authorization-only probes retain the known baseline hard-deny visibility gap for rejected nested substitutions; no command ran.
- [x] 2.4 Test a compound with no body command; verify its redirect still meets file path and file-tool mode checks.

## 3. Consent and launch

- [x] 3.1 Query existing file-tool grants for each compound file operation; test that a file grant covers no body command and a command grant covers no redirect.
- [x] 3.2 Bind one-time approval to the full shell source and each uncovered file operation; test that changed source or target does not match.
- [x] 3.3 Add compound targets to pre- and post-consent launch path captures; test that a changed path or link target stops process start.

## 4. Documentation and verification

- [x] 4.1 Update `docs/architecture/tool-authorization.md` and the mapped shell-approval guidance; verify skill version `2.134.0`. The operator waived runtime LLM evaluation. The cancelled partial run is not a pass. Architecture, guidance, and the canonical spec are updated. Skill `2.134.0` is verified. The user waived runtime eval; no pass is claimed.
- [x] 4.2 Run focused security, actor, and launch tests; record exact commands and results.
- [x] 4.3 Run OpenSpec validation, Slopwatch, copyright verification, and `git diff --check`; retain every failure.
