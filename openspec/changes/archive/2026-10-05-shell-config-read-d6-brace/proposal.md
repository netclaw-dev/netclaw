## Why

The second review of #2337 found two bypasses of the D6 shell read. The parser
reports a brace word (`{netclaw,secrets}.json`) as one exact path, but Bash
expands it to `secrets.json`. Program text can spell the config directory as
`dir//config`, `dir/./config`, `dir/x/../config`, or with quotes that split
the name, and the text screen did not see it. The TA-6 text also had a wrong
scenario and an overstated directory rule. Traceability: PRD-002 (gateway
security envelope).

## What Changes

- A word with a brace keeps the denial. It does not leave the text screen, and
  it does not make a program read-only.
- The guarded-directory screen also reads the text with `//`, `/./`, and
  `name/../` collapsed, and the unquoted value of each argument.
- The scenario "A grant never opens a protected path" uses `secrets.json`.
- The directory rule states what the code does. The recursive and brace read
  from a parent of the config directory is a known gap that #2341 owns.
- The TA-6 traceability row names the D6 gate and its mutation gate.

In scope: TA-6 text, its scenarios, and the two bypass fixes. Out of scope:
the #2341 gaps and a parser for program text.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-6 shell read rules and the protected-path scenario.

## Impact

- Code: `ToolPathPolicy`, `ToolAccessPolicy`.
- Security: secrets, webhook route files, and keys stay denied in each brace
  and spelling form. A `jq` filter with a brace (`jq '{a: .x}' file`) is not a
  read-only program, so a config read with such a filter stays denied.
- Operations: no config or runbook change.
