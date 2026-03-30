## 1. Build the enforced scanner

- [x] 1.1 Implement a production `ISkillContentScanner` that classifies skill files by path role, enforces UTF-8 text and size limits, rejects binary/executable payloads, and returns explicit rejection reasons.
- [x] 1.2 Integrate prompt-injection detection into prompt-bearing skill files with fail-closed behavior on detector failure, and replace the runtime DI registration of `NoOpSkillContentScanner`.

## 2. Apply enforcement on write paths

- [x] 2.1 Update `SkillManageTool` so `create`, `edit`, `patch`, and `write_file` all scan candidate content before persisting and preserve existing files on rejection.
- [x] 2.2 Update `SystemSkillSyncService` to stage downloaded skill versions, scan `SKILL.md` plus resource files, and only swap the on-disk version when every file passes.

## 3. Document and verify behavior

- [x] 3.1 Add unit and integration tests for allowed markdown/scripts, rejected binary payloads, prompt-injection rejection, detector failure handling, and sync rollback semantics.
- [x] 3.2 Update `feeds/skills/.system/files/skill-authoring/SKILL.md` to explain the enforced content-scan rules and resource-file constraints.
- [ ] 3.3 Run relevant test suites, `dotnet slopwatch analyze`, and `./evals/run-evals.sh` if the system skill text changes during implementation.
