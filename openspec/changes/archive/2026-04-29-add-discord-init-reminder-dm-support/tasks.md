## 1. Discord DM reminder delivery

- [x] 1.1 Add Discord `IReminderTargetResolver` support for canonical DM destination validation and persistence wiring in `set_reminder`.
- [x] 1.2 Extend reminder execution routing to support Discord `current_session` trusted turn delivery through gateway actor boundaries.
- [x] 1.3 Preserve required-delivery observation timeout handling for Discord reminder deliveries and emit failure diagnostics on missed delivery.

## 2. Slack-like authorization controls for Discord

- [x] 2.1 Extend ACL evaluation mapping so Discord DM sender/channel metadata uses existing default-deny allow checks.
- [x] 2.2 Enforce reminder audience-bound minting rules for Discord session sources (inherit omitted audience, reject broader audience).
- [x] 2.3 Add/adjust policy diagnostics for Discord deny and reminder-mint rejection paths to keep failure reasons operator-visible.

## 3. netclaw init pipeline support

- [x] 3.1 Add Discord onboarding step(s) in `netclaw init` flow to collect required Discord credentials when Discord is enabled.
- [x] 3.2 Add init validation and config writing for Discord adapter settings with fail-closed behavior on missing required fields.
- [x] 3.3 Generate baseline Discord ACL starter policy during init so unlisted Discord identities remain denied by default.

## 4. Verification and documentation

- [x] 4.1 Add tests for Discord DM reminder channel-kind and current-session delivery behavior, including required-delivery timeout failure.
- [x] 4.2 Add tests for Discord ACL parity and reminder audience authorization semantics.
- [x] 4.3 Add tests for init pipeline Discord config/ACL generation and validation failure handling.
- [x] 4.4 Update operator-facing docs/help for Discord reminder setup and init configuration outputs.
