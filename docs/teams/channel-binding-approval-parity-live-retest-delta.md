# Teams channel-binding parity live retest delta

Status: **NOT EXECUTED IN THIS CHANGE**.

Historical Phase 1 Teams evidence remains valid. This delta requests only the
boundaries changed by the channel-binding and approval-parity refactor.

1. Send one representative Personal approval request and select `Once` or
   `Deny`. Confirm one native terminal card and one session outcome.
2. Let one pending card expire, then use the replacement card. Confirm the old
   card cannot act, the replacement resolves the same pending request once, and
   no implicit core Deny is created by expiry.
3. Interrupt one approval feedback response before acknowledgement, retry the
   returned same-option card, and restart once while forwarding is uncertain.
   Confirm the session resolves once and no selected tool executes twice.

Record only sanitized structural outcomes and counters. Do not change Azure
Bot, Entra, Teams package, tenant permissions, routes, secrets, or operator
configuration while carrying out this delta.
