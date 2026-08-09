## 1. Package and display contract

- [x] 1.1 Update the central ShellSyntaxTree version to `0.3.0-alpha.1`.
- [x] 1.2 Use typed heredoc and here-string operations for raw display fallback.
- [x] 1.3 Add focused display tests for full heredoc and `<<<` disclosure.

## 2. Constrained stdin grammar

- [x] 2.1 Accept only argument-free `cat` with a literal heredoc or bounded here string.
- [x] 2.2 Keep unknown data, other receivers, arguments, wrappers, and incomplete facts strict.
- [x] 2.3 Add focused analysis and policy tests for each stdin boundary.

## 3. Approval review matrix

- [x] 3.1 Add strict cases for Bash command-resolution mutation.
- [x] 3.2 Add strict cases for unsupported reserved execution forms.
- [x] 3.3 Add allow and prompt cases for heredoc and here-string data.
- [x] 3.4 Update and inspect the review-table snapshot.

## 4. Verification and tracking

- [x] 4.1 Run the focused Netclaw.Security test suite.
- [x] 4.2 Run the actor approval-matrix test suite.
- [x] 4.3 Run Slopwatch, header verification, and strict OpenSpec validation.
- [x] 4.4 Update `IMPLEMENTATION_PLAN.md` with the delivered package and matrix evidence.
