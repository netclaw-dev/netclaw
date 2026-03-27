## 1. Restart coordination

- [x] 1.1 Add a daemon restart coordinator plus ingress gate and route `ConfigWatcherService` through it instead of calling `StopApplication()` directly.
- [x] 1.2 Persist a restart manifest for the active-session set captured after ingress closes, and raise the host shutdown budget so drain has enough time to finish.

## 2. Session drain behavior

- [x] 2.1 Add a restart-specific drain message path to `LlmSessionActor` that keeps idle passivation behavior separate from config-triggered restart drain.
- [x] 2.2 Make ready sessions passivate immediately, let processing/compacting sessions finish or fail their in-flight work within the drain window, and stop from the last durable checkpoint on timeout.
- [x] 2.3 Return a restart-in-progress rejection for new `SendUserMessage` and retry-inducing delivery feedback once restart drain has begun.

## 3. Startup recovery

- [x] 3.1 Add a startup recovery hosted service that reads the restart manifest, rewarms the previously active sessions through the session manager, and clears the manifest after recovery.
- [x] 3.2 Inject a transient restart continuity notice into warmed sessions so the next turn explains recovery from the last durable checkpoint.

## 4. Adapter and lifecycle integration

- [x] 4.1 Update daemon-managed ingress paths so Slack, SignalR, and other adapter entry points reject new work consistently while restart drain is active.
- [x] 4.2 Ensure lifecycle notifications, session catalog state, and restart logs distinguish successful drains from timeout-based restart completion.

## 5. Verification and docs

- [x] 5.1 Add daemon/service tests covering valid config change drain, invalid config no-restart, ingress rejection during drain, and timeout-driven restart.
- [x] 5.2 Add actor integration tests covering restart drain from ready, processing, and compacting states plus recovery from the last durable checkpoint.
- [x] 5.3 Update `docs/spec/SPEC-011-daemon-architecture.md`, related PRD/spec references, and any user-facing diagnostics text to match the new coordinated restart behavior.
- [x] 5.4 Run `dotnet test` for affected projects and `dotnet slopwatch analyze` after implementation changes.
