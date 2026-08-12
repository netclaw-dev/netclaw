## 1. Typed wire model

- [x] 1.1 Add closed token-prefix, legacy-exact, and non-shell entry forms.
- [x] 1.2 Add source-generated wire DTOs, a strict raw JSON reader, and exact
  serialization snapshots, including empty-token-array rejection.
- [x] 1.3 Add whole-file checks for duplicates, maps, members, enums, tokens,
  paths, and timestamps.

## 2. Version 2 migration

- [x] 2.1 Parse and validate version 2 before any file-system change.
- [x] 2.2 Migrate valid shell entries to `LegacyExact` without authority
  growth.
- [x] 2.3 Preserve valid non-shell entries and omit unrepresentable shell
  phrases with one bounded diagnostic.
- [x] 2.4 Add a bounded cross-process lock and no-link sibling checks.
- [x] 2.5 Create a byte-identical `.v2.bak`, flush the temporary file, compare
  the source again, and replace the active store atomically.
- [x] 2.6 Add deterministic lock, backup, replace, retry, and cache tests.

## 3. Store consumers

- [x] 3.1 Return typed ready or unavailable store status to the actor API.
- [x] 3.2 Update daemon, CLI, and TUI code to use the shared typed comparer and
  formatter.
- [x] 3.3 Use ShellSyntaxTree for `shell_execute` trust phrases and keep
  non-shell `--tool` entries exact.
- [x] 3.4 Document list, revoke, migration, and manual backup recovery.

## 4. Validation

- [x] 4.1 Run strict OpenSpec validation and focused configuration, actor, CLI,
  and TUI tests.
- [x] 4.2 Run Release build, full tests, header verification, and Slopwatch.
- [x] 4.3 Obtain adversarial review and resolve all findings.
- [x] 4.4 Update `IMPLEMENTATION_PLAN.md` and the parent structured-policy task
  list with the final result.
