## 1. Configuration contract

- [x] 1.1 Add the sensitive MCP OAuth client-secret property and verify encrypted configuration binding through a focused test.
- [x] 1.2 Mark the secret-only property invalid in the public schema and verify strict validation rejects it there.

## 2. Secure CLI persistence

- [x] 2.1 Add `--client-secret` parsing and reject an unpaired secret before writes through CLI tests.
- [x] 2.2 Store and reload the encrypted profile secret, and verify public configuration and CLI output contain no secret.
- [x] 2.3 Update MCP CLI help and verify the focused command tests pass.
- [x] 2.4 Remove an omitted client secret during profile replacement without changing other server profiles.
- [x] 2.5 Restore the prior public configuration when a secret write fails, and verify the failure path.

## 3. OAuth runtime identity

- [x] 3.1 Pass the configured secret through token-cache identity and SDK options, and verify focused manager tests pass.
- [x] 3.2 Keep configured secrets out of OAuth token records, and verify rotation selects the current profile secret.
- [x] 3.3 Extend the programmable OAuth server for confidential clients and verify exchange and restart refresh use the configured secret.
- [x] 3.4 Report the configured secret as present in safe refresh diagnostics without storing or printing its value.

## 4. Operator contract

- [x] 4.1 Update the `netclaw-operations` system skill. Correct external release documentation after beta.4 ships.

## 5. Validation

- [x] 5.1 Validate the OpenSpec change and verify all tasks match the specification.
- [x] 5.2 Run focused tests, the full solution tests, a release build, Slopwatch, and the header check.
- [x] 5.3 Run the behavioral eval suite when provider settings exist, or record the absent required settings.
