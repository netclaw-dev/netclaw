## Why

The adversarial review of #2337 found three gaps in the first D6 text. Program
text that names the config directory (a `jq` module search path, `python3 -c`)
could read `secrets.json`. The webhook route files hold the verification
secret in plain text. A harmless `2>/dev/null` turned a config read into a
denial. The owner confirmed that webhook route files are credentials.
Traceability: PRD-002 (gateway security envelope).

## What Changes

- The webhook route files stay read-denied with `secrets.json` and the keys.
- Shell text that names the config directory stays denied, as before D6.
  Only an exact path argument of a read-only program that names one file
  below the directory leaves the text screen.
- A redirect no longer makes a program not read-only. A redirect that writes
  gets the write check for its target only.

In scope: TA-6 text and its scenarios. Out of scope: write authority and the
`grep -r token ~/.netclaw` gap from before D6 (a separate issue).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tool-authorization`: TA-6 path protection and the D6 shell read.

## Impact

- Code: `DaemonToolPathPolicyFactory`, `ToolPathPolicy`, `ToolAccessPolicy`.
- Security: credentials (secrets, webhook route files, keys) stay denied in
  every form, including program text. Writes stay denied.
