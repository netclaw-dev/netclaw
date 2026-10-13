## 1. Contract and tracker alignment

- [x] 1.1 Update PRD-004 and SPEC-004 with the plugin-level lifecycle, portable format, compatibility format, and failure rules.
- [x] 1.2 Add shared plugin and source identity terms to the engineering glossary.
- [x] 1.3 Update Netclaw issues #2134 and #2135, and website issue #119, with the approved contract and PR stack links.

## 2. Plugin-neutral source and state contracts

- [x] 2.1 Replace Git skill plugin configuration types with managed plugin source types and source IDs.
- [x] 2.2 Update the JSON schema and configuration round-trip tests for `auto`, `agent-plugin`, and `codex` formats.
- [x] 2.3 Replace Git skill plugin API and durable state contracts with plugin-neutral names and manifest identity fields.
- [x] 2.4 Update SQLite migration, receipt, rejection, restart, and stale-state tests for the canonical representation.

## 3. Portable package interpretation

- [x] 3.1 Add a package selector that chooses one explicit format and does not fall through after selection.
- [x] 3.2 Add an Agent Plugins 1.0.0 adapter for root manifest validation and fixed `skills/` discovery.
- [x] 3.3 Move Codex manifest rules into a compatibility adapter without duplicate Git or archive policy.
- [x] 3.4 Add valid, invalid, multi-manifest, dotted-name, non-SemVer, and unsupported-component fixtures.
- [x] 3.5 Add portable skill failure-isolation tests and complete-candidate scanner rejection tests.

## 4. Managed plugin sync participant

- [x] 4.1 Extract startup publication, update policy, rejection state, alerts, and cleanup into a managed plugin sync participant.
- [x] 4.2 Keep the existing sync actor as the sole pass and waiter owner.
- [x] 4.3 Preserve one final inventory refresh and the documented source precedence.
- [x] 4.4 Run restart, interleaving, failure isolation, and previous-package recovery tests.

## 5. Plugin daemon API

- [x] 5.1 Replace `/api/skills/plugins` with authenticated `/api/plugins` routes.
- [x] 5.2 Return source ID and manifest name as separate list fields.
- [x] 5.3 Preserve bounded RFC 9457 details for validation, conflict, acquisition, scan, and timeout failures.
- [x] 5.4 Update endpoint authorization, persistence-block, and error contract tests.

## 6. Plugin CLI

- [x] 6.1 Replace `netclaw skill plugin` with `netclaw plugin` and add the update action.
- [x] 6.2 Add stable `netclaw plugin list --json` output with no prose.
- [x] 6.3 Parse safe daemon problem details and keep a bounded status fallback.
- [x] 6.4 Update CLI unit tests, real-process tests, help text, exit codes, and confirmation tests.

## 7. Guidance and verification

- [x] 7.1 Update `skill-authoring` and `netclaw-operations`, and increment each changed skill version.
- [x] 7.2 Run targeted configuration, package, sync, endpoint, CLI, and process tests.
- [x] 7.3 Run the full .NET test suite, Slopwatch, file-header verification, and `git diff --check`.
- [ ] 7.4 Run the behavioral eval suite and the native smoke harness with the required environment.
- [x] 7.5 Run OpenSpec verification and record all remaining evidence limits before archive.

## Verification evidence limits

- The full .NET suite passed 8,543 tests and skipped 22 environment-specific tests.
- The plugin management and public repository smoke scenarios passed all eight checks.
- The eval suite lacks the required provider type, endpoint, and model variables.
- The native tapes cannot run because Chromium reports `No usable sandbox` in this container.
- OpenSpec verification found no implementation or design divergence.
- Task 7.4 remains incomplete until an eligible environment runs both blocked gates.
