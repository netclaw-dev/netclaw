# inbound-webhooks Delta Spec — Config UI Onboarding

## Purpose

Reconcile the shipped `InboundWebhooksConfigViewModel` against the existing
inbound-webhooks runtime spec. The existing runtime requirements are unchanged.
This file adds requirements that were implemented but not previously specified:
the `Webhooks.ExecutionTimeoutSeconds` top-level config field and the
non-blocking advisory emitted when the feature is enabled without any active
routes.

## ADDED Requirements

### Requirement: Execution timeout bounding webhook-triggered autonomous runs

The top-level `netclaw.json` config SHALL support a `Webhooks.ExecutionTimeoutSeconds`
field that sets an upper bound (in seconds) on an inbound-webhook-triggered
autonomous run. The field MUST accept only integer values in the range 1–3600
inclusive, and SHALL default to 300 when absent or unset. An out-of-range or
non-integer value SHALL be rejected before the config is persisted, and the UI
MUST surface the validation error without saving.

#### Scenario: Valid timeout is accepted and persisted

- **WHEN** an operator enters a whole-number timeout value between 1 and 3600 in
  the inbound-webhooks config UI and saves
- **THEN** `Webhooks.ExecutionTimeoutSeconds` is written to `netclaw.json` with
  the entered value
- **AND** the UI reports a success status

#### Scenario: Out-of-range timeout is rejected before persistence

- **WHEN** an operator enters a timeout value outside the range 1–3600 (e.g., 0
  or 9999) and attempts to save
- **THEN** the config file is not modified
- **AND** the UI surfaces an error message indicating the valid range

#### Scenario: Non-integer timeout is rejected before persistence

- **WHEN** an operator enters a non-integer string (e.g., `"fast"` or `"30.5"`)
  in the execution-timeout field and attempts to save
- **THEN** the config file is not modified
- **AND** the UI surfaces an error message indicating that a whole number is
  required

#### Scenario: Missing timeout defaults to 300 on load

- **GIVEN** `netclaw.json` does not contain `Webhooks.ExecutionTimeoutSeconds`
- **WHEN** the inbound-webhooks config UI loads
- **THEN** the timeout field is pre-populated with `300`

### Requirement: Enable-without-routes emits non-blocking advisory

Setting `Webhooks.Enabled = true` when no routes are enabled SHALL persist the
toggle and SHALL emit a non-blocking advisory directing the operator to author a
route with `netclaw webhooks set`. This MUST NOT block or fail the save: the
gateway fails closed per-route at runtime (returning `404 Not Found` for all
requests) until routes exist, so enabling without routes is the intended setup
order, not an error condition.

#### Scenario: Enabling with no active routes persists toggle and shows advisory

- **GIVEN** inbound webhooks are currently disabled
- **AND** no route files exist under `config/webhooks`, or all existing routes
  are disabled or invalid
- **WHEN** an operator enables the feature and saves
- **THEN** `Webhooks.Enabled = true` is written to `netclaw.json`
- **AND** the UI displays a warning-tone advisory instructing the operator to add
  a route with `netclaw webhooks set`
- **AND** the save succeeds (is not blocked or treated as an error)

#### Scenario: Enabling with at least one active route shows success status

- **GIVEN** at least one route file under `config/webhooks` is enabled and valid
- **WHEN** an operator enables the feature and saves
- **THEN** `Webhooks.Enabled = true` is written to `netclaw.json`
- **AND** the UI displays a success-tone status message
- **AND** no advisory is shown
