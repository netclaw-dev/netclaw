## 1. Encryption policy

- [x] 1.1 Add the plaintext-leaf predicate and path-aware traversal to the secrets writer, and verify the writer tests pass
- [x] 1.2 Update `netclaw secrets set` to state the exemption and reject a non-date expiry, and verify the CLI tests pass
- [x] 1.3 Verify the secrets doctor check does not warn on the plaintext expiry

## 2. Expiry storage

- [x] 2.1 Add the expiry parse helper and move the expiry write into one secrets update, and verify the persistence tests pass
- [x] 2.2 Remove the expiry write from the provider credential writer and read it from secrets in the provider command, and verify the CLI tests pass
- [x] 2.3 Update the schema description and verify the schema tests pass
- [x] 2.4 Verify the producer-to-binder round trip with the real protector

## 3. Refresh service

- [x] 3.1 Port the stored-token read, spent-token set, write-back, and error log, and verify the unit tests pass
- [x] 3.2 Add the cross-process refresh tests and the write-back killer test, and verify them
- [x] 3.3 Verify the write-back test fails when the write-back is removed

## 4. Legacy migration

- [x] 4.1 Add the retired-key helpers, the doctor check, the doctor fix, and the startup warning, and verify the doctor tests pass
- [x] 4.2 Verify a refresh persists nothing to `netclaw.json` (byte-identical file test)

## 5. Mutation gate

- [x] 5.1 Add the mutation tests, script, CI entry, and TOOLING.md entry, and verify every mutant dies

## 6. Docs and skill

- [x] 6.1 Update `docs/spec/configuration.md` and the `netclaw-operations` skill with a version bump
- [x] 6.2 Run the verification commands and record the results
