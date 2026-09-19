## 1. Modern Teams presentation model

- [x] 1.1 Create the SDK-free pending, elevated, terminal, and neutral card variants; verify option order, labels, and callbacks remain session supplied.
- [x] 1.2 Create the schema-1.5 header, banner, table, footer, and `speak` payload; verify the emitted JSON tree against the supplied visual language.

## 2. Terminal presentation integration

- [x] 2.1 Render granted and explicit-deny cards from the accepted option and authoritative result timestamp; verify terminal cards contain no actions.
- [x] 2.2 Render expiry as a warning terminal card with a fresh pending replacement; verify no core deny is forwarded.
- [x] 2.3 Keep already-processed and unavailable callbacks neutral; verify they never claim granted or denied state.

## 3. Regression coverage and documentation

- [x] 3.1 Add focused renderer and SDK-edge tests for 1.5 payloads, tables, icons, dynamic actions, bounded values, escaping, and terminal states.
- [x] 3.2 Preserve existing Teams approval and cross-channel regression coverage; verify the callback contract, nonce protection, and Personal-only shell boundary remain unchanged.
- [x] 3.3 Update the Teams integration runbook and validate this change with strict OpenSpec validation and the repository quality gates.
