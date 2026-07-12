## 1. Canonical configuration and compatibility

- [x] 1.1 Add named definition and role-reference configuration types plus one resolver for legacy and canonical shapes
- [x] 1.2 Add deterministic, conflict-detecting legacy migration with atomic persistence and backup behavior
- [x] 1.3 Update the JSON schema to accept legacy or canonical models while rejecting mixed/invalid shapes
- [x] 1.4 Route daemon, CLI, doctor, provider rename, wizard, and TUI consumers through resolved canonical configuration

## 2. Operator workflows

- [x] 2.1 Update model CLI commands to create/edit definitions and switch roles without mutating definitions
- [x] 2.2 Update the model-manager TUI and initialization writer to emit canonical configuration
- [x] 2.3 Update `netclaw-operations` guidance and CLI help, including migration and rollback behavior

## 3. Automated proof

- [x] 3.1 Add legacy load, conflict rejection, canonical round-trip, and role A→B→A preservation tests
- [x] 3.2 Add CLI/TUI tests proving invalid references are rejected before persistence
- [x] 3.3 Add isolated latest-stable-container → local-image upgrade smoke and semantic assertions
- [ ] 3.4 Run targeted/full tests, native TUI smoke, evals, Slopwatch, and copyright verification
- [x] 3.5 Validate OpenSpec implementation alignment and prepare spec synchronization
