## Context

Before this change, shell `Once` keys covered approval candidates but not the exact structured command. Session history already stored the original arguments.

## Decisions

1. Read source with the canonical shell argument reader. Accept `Command` and the supported legacy `command` field, including JSON element values. Hash the exact string with a stable, case-sensitive encoding.
2. Keep only the derived key on the call-local `ToolApprovalContext`. Do not add it to requests, persistent grants, journal data, or protocol records.
3. Add the key to shell `Once` identity. Keep candidate keys, tool-name binding, and one-use clearing. Keep non-shell keys unchanged.
4. On redrive, match each resolved call ID to one original history call. Require the canonical tool name to match. Missing, malformed, or ambiguous source stops the retry.
5. Never derive source identity from display text. Display text can redact or truncate the original value.

Schematic flow; normal admission and persistence steps are omitted:

```text
structured call -> canonical Command extraction -> call-local source key -> Once comparison
       |
       +-> history ArgumentsJson -> recovered call -> same extraction
```

Positive case: the exact source retries once. A changed source with equal candidates needs consent again. Missing or ambiguous history stops recovery.

No journal migration is needed. The original structured command already exists in session history. Persistent grant identity stays unchanged.
