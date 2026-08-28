## 1. TUI terminal-error recovery

- [x] 1.1 Update the Chat view model terminal-error transition and verify focused headless tests prove retry-ready state.
- [x] 1.2 Run the focused Chat TUI tests and verify `ErrorOutput` clears pending interaction state and generation state.

## 2. Deterministic smoke LLM server

- [x] 2.1 Add the loopback-only `Netclaw.SmokeLlmServer` executable and verify solution build succeeds.
- [x] 2.2 Implement health, models, JSON completion, SSE completion, validation, and safe request-record contracts with HTTP tests.
- [x] 2.3 Add the executable to native publish outputs and verify the published binary is runnable.

## 3. Native harness migration

- [ ] 3.1 Add smoke LLM process lifecycle and artifact collection to the native harness and verify readiness failure is actionable.
- [ ] 3.2 Convert broad smoke provider setup, tapes, assertions, and scenarios to `openai-compatible` and verify no broad path reads `SMOKE_OLLAMA_MODEL`.
- [ ] 3.3 Remove broad Ollama setup from required smoke workflows and verify CI publishes the smoke LLM executable.

## 4. Isolated Ollama contract

- [ ] 4.1 Add a manual or scheduled Ollama contract workflow and verify it performs discovery plus one no-tool completion outside pull-request gating.

## 5. Validation

- [ ] 5.1 Run smoke-server HTTP tests, Chat TUI tests, and `scripts/smoke/run-smoke.sh init-wizard`.
- [ ] 5.2 Run `dotnet test`, `dotnet slopwatch analyze`, `pwsh ./scripts/Add-FileHeaders.ps1 -Verify`, `git diff --check`, and OpenSpec validation.
