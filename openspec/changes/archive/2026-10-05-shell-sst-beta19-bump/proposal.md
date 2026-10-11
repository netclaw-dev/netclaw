## Why

ShellSyntaxTree 0.4.0-beta.18 and 0.4.0-beta.19 parse bounded Bash
arithmetic, loop `break` and `continue`, and ANSI-C quotes. They also fix two
misreads: a brace word was one exact path, and `((...))` was two subshells.
Beta.19 reports whether Bash can glob each word. Netclaw must adopt these facts
and close two read paths to the credential store: an ANSI-C spelling and a
brace word with a global `cat` grant.

## What Changes

- Update ShellSyntaxTree from 0.4.0-beta.17 to 0.4.0-beta.19.
- A bounded `$((...))` is data. Arithmetic that can run code and `((...))`
  stay unresolved.
- `break` and `continue` are Bash data commands.
- A decoded ANSI-C path gets the decision of its literal twin. Each proved
  path value also gets the default credential store text hints.
- An output operand is data when Bash cannot glob it (`MayPathnameExpand`),
  which replaces the raw double-quote scan.
- A Bash operand that can glob, with an unknown value and no glob-free
  authored value, makes its command one exact candidate. Decision D1 does not
  cover it.
- A brace program word is unresolved. In an unattended run it is denied, not
  given rewrite advice. Neither outcome runs the call.

## Capabilities

### New Capabilities

### Modified Capabilities
- `tool-authorization`: TA-7 states the beta.18 and beta.19 facts and the new
  pathname-expansion rule.

## Impact

- `Directory.Packages.props`, `ShellCommandAnalysis`, `ToolPathPolicy`, and
  `ShellVerbPolicyData`.
- The review catalog, its table, the outcome ledger, the mutation script, and
  the operations skill and runbook.
