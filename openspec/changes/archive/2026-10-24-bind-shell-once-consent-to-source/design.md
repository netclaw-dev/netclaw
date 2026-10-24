## Context

See [proposal.md](proposal.md). `Once` currently hashes parsed candidates. Session history also keeps the original structured tool arguments, so the retry can bind to source without a new journal field.

## Goals / Non-Goals

**Goals:**

- Bind shell one-time consent to the exact structured `Command` string.
- Use the same source key for live authorization and recovered redrive.
- Preserve persistent grant identity and non-shell one-time consent.

**Non-Goals:**

- Change prompt display, candidate extraction, grant matching, or the journal schema.
- Bind consent to mutable display text or to an additional raw source copy.

## Decisions

1. Extract source with the canonical shell argument reader. It accepts the current `Command` field and the supported legacy `command` field, including JSON element values. Hash that exact string with a stable, case-sensitive encoding.
2. Carry only the derived source key on the call-local `ToolApprovalContext`. Preserve it through context copies. Do not add it to approval requests, persistent grants, or protocol records.
3. Add the source key to shell one-time keys. Keep the candidate keys, tool-name binding, and one-use clearing behavior. Non-shell keys stay unchanged.
4. On redrive, map each resolved call ID to one original call in assistant history. Require the original tool name to match the recovered canonical name. Derive the key from its structured arguments. Missing, malformed, or ambiguous source makes redrive unsafe.
5. Do not fall back to display text. Display text can redact or truncate source and does not identify the original command.

The flow is:

```text
structured call -> canonical Command extraction -> call-local source key
       |                                      |
       +-> prompt and live retry              +-> Once key comparison
       |
       +-> history ArgumentsJson -> recovered call -> same extraction
```

## Risks / Trade-offs

- [Risk] A source change that preserves candidates now needs consent. This is the intended limit of a one-call answer.
- [Risk] Old or damaged history may lack a usable command. Netclaw abandons that redrive instead of using a display fallback.
- [Trade-off] The source hash adds one small call-local value. It avoids storing a second raw source string or changing durable state.

## Migration Plan

No journal migration is required. Existing open approvals derive the source from the assistant tool call that already sits in session history.
