## 1. Tests

- [x] 1.1 Add failing endpoint tests: 30 refused requests, then a valid proof gets `200` on both local-control endpoints
- [x] 1.2 Add a test that the refusal count resets after the window

## 2. Daemon

- [x] 2.1 Make `LocalControlRequests` a singleton that reads the body, validates the proof, and counts only refusals
- [x] 2.2 Remove the `local-control` rate-limit policy and use `LocalControlRequests.AuthorizeAsync` in both handlers

## 3. Gates and docs

- [x] 3.1 Add the `AuthorizeAsync` result line to the local-control mutation gate and kill its three mutants
- [x] 3.2 Update `TOOLING.md` for the gate
- [x] 3.3 Sync the delta into `openspec/specs/local-control-proof/spec.md`
