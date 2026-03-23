## 1. Lifecycle Wiring

- [x] 1.1 Move session deactivation reporting from `SessionPipeline` termination to the `LlmSessionActor` idle-timeout passivation path.
- [x] 1.2 Update session catalog status transitions to use `active` / `inactive` and preserve `last_activity` during status-only transitions.

## 2. Verification And Follow-up

- [x] 2.1 Update daemon-side session catalog tests for deactivation and timestamp preservation.
- [x] 2.2 Add actor-level coverage that idle timeout deactivation fires only when the session actually passivates.
- [x] 2.3 Run targeted tests and `dotnet slopwatch analyze`.
- [x] 2.4 Comment on issue `#326` describing this change as groundwork for future graceful drain/restart work.
