## 1. Approval terminal presentation

- [x] 1.1 Add the bounded, sanitized optional Teams presenter label to the SDK-free approval callback and terminal-card rendering path; verify canonical sender identity, journal events, callback data, and telemetry remain unchanged.
- [x] 1.2 Change the granted terminal-card field to `Execution State: Execution Approved`; verify visible and screen-reader text make no execution-success or completion claim.

## 2. Regression coverage

- [x] 2.1 Add translator and renderer tests for valid labels, fallback behavior, unsafe and overlong input, canonical sender identity, and exact terminal text.
- [x] 2.2 Extend interactive routing tests to prove deny remains terminal and non-executing, approve-once remains exactly once, expiry replacement remains singular, and replay remains neutral.

## 3. Validation

- [x] 3.1 Run focused Teams renderer, translator, and interactive-routing tests; record the exact test counts.
- [x] 3.2 Run the solution build and test suite, Slopwatch, file-header verification, whitespace check, and strict OpenSpec validation.
