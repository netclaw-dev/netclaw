## 1. Eval Contract and Baseline

- [x] 1.1 Add exact skill-load method helpers and negative `SKILL.md` file-read assertions to the eval harness.
- [x] 1.2 Add server-feed logical loading and explicit physical-inspection eval cases.
- [x] 1.3 Capture focused pre-change eval results with the configured provider.

## 2. Logical Index Contract

- [x] 2.1 Add failing registry tests for logical tool guidance and absence of physical roots.
- [x] 2.2 Change index generation and callers to use the origin-free logical contract.
- [x] 2.3 Add tool tests proving server-feed skills and resources resolve by logical name, including routed-skill guidance.

## 3. Authoritative Inventory Refresh

- [x] 3.1 Add an inventory refresher that resolves live enabled server-feed sources and serializes complete refreshes.
- [x] 3.2 Publish registry replacements as complete snapshots safe for concurrent readers.
- [x] 3.3 Route startup, system/feed sync, directory watching, and `skill_manage` mutations through the refresher.
- [x] 3.4 Add tests for mutation preservation, late feed directory discovery, precedence, and complete snapshot visibility.

## 4. Guidance and Documentation

- [x] 4.1 Replace physical skill-loading examples in runtime identity guidance.
- [x] 4.2 Update and version-bump `skill-authoring`; align operations references and repository guidance.
- [x] 4.3 Update eval documentation for exact method assertions and retained results.

## 5. Verification

- [x] 5.1 Run targeted actor and daemon skill tests.
- [x] 5.2 Run the solution test suite, Slopwatch, copyright verification, and whitespace validation.
- [x] 5.3 Run focused and full behavioral evals with the same provider settings, or document the external credential blocker.
- [x] 5.4 Validate implementation against the OpenSpec change and prepare it for sync/archive.
