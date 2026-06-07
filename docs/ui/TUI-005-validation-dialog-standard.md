# TUI-005: Validation Dialog Standard

Source PRDs: `PRD-004`

Related docs:

- `docs/ui/TUI-004-search-config-progressive-disclosure-poc.md`
- `docs/ui/TUI-002-netclaw-config-wireframes.md`

Status: implementation standard for URL, endpoint, and live probe validation.

## Scope

Use this pattern for TUI flows that collect a URL, endpoint, remote service,
provider, or credential and then run a live validation probe before saving.

Examples:

- Search provider endpoints
- model provider endpoints and discovered model lists
- skill server URLs and discovered skill counts
- webhook targets when live delivery validation is added

## Standard Flow

```text
User enters URL / endpoint
  |
  v
Static validation
  |-- invalid shape -> stay on field, show inline/status error, do not probe
  |
  v
Live validation probe
  |-- running -> show spinner / validating screen
  |-- success -> show success result with discovered facts, then continue/save
  |-- failure -> show validation warning dialog

Validation warning dialog
  |-- Retry validation -> run the same probe again
  |-- Back to edit     -> close dialog and keep the draft unchanged
  |-- Save anyway      -> persist only if structural validation still passes
```

## Dialog Standard

Warning dialogs use exactly these actions in this order:

1. `Retry validation`
2. `Back to edit`
3. `Save anyway`

The first highlighted action is always retry. `Save anyway` must be explicit; a
plain second `Enter` is not a hidden override.

Do not duplicate probe failures. The dialog owns the failure message, and the
status line should be empty while the dialog is visible.

## Input Fields

Validated text fields must show an obvious focused input affordance using the
native text input cursor. A fake rendered cursor marker is not acceptable when a
native input control is available.

Validated text fields must wrap the native Termina text input control for text
editing. Do not reimplement text editing with a rendered text node. The native
input owns cursor movement, Home/End, paste behavior, placeholder rendering,
password masking, and the blinking cursor; the validated layer only stages draft
values and intercepts commit triggers such as Enter.

## Validation Result Shape

Live validation should produce a result with this conceptual shape:

```text
status: success | warning | error
message: human-readable validation result
facts: optional discovered metadata
```

`facts` are optional but should be preserved when available. They are how flows
show discovered models, discovered skill counts, server versions, or warnings
without creating one-off pages.

## Optional Facts

For model/provider validation, useful facts include:

- provider reachable
- discovered model count
- default model found
- context window if known

For skill server validation, useful facts include:

- discovery endpoint reachable
- discovered skill count
- server name/version if exposed
- warnings from malformed skill entries

After save, list and detail pages should carry forward useful facts instead of
only saying that a service is configured.

## Current Implementations

- Search uses the warning dialog for failed live search validation.
- Skill Sources uses the same dialog for failed skill server discovery probes.

Future config pages should reuse `NetclawValidationDialogViews` rather than
building local copies of the panel and action list.
