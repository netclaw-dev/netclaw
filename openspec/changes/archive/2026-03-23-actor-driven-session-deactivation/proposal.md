## Why

Session catalog status is currently tied to individual channel pipeline teardown instead of the `LlmSessionActor` lifecycle. That makes multi-subscriber sessions appear inactive too early and rewrites `last_activity` during resume/rematerialization, which weakens session inspection for PRD-001 and PRD-003.

## What Changes

- Move session deactivation reporting from `SessionPipeline` stream termination to the `LlmSessionActor` idle-timeout passivation path.
- Standardize catalog status on `active` / `inactive` and stop using pipeline-local teardown as the inactivity signal.
- Preserve `last_activity` for existing sessions when they are reactivated or deactivated without a new turn/output event.
- Add actor and catalog test coverage for deactivation semantics and note the follow-on relationship to issue `#326`.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `netclaw-session`: session lifecycle reporting now uses actor-driven deactivation instead of per-pipeline stream completion.
- `session-resume`: session catalog listings now expose stable `active` / `inactive` state without mutating `last_activity` on resume/deactivation transitions.

## Impact

- PRD references: `docs/prd/PRD-001-netclaw-mvp.md`, `docs/prd/PRD-003-operator-ux-ops-console.md`, `docs/prd/PRD-004-cli-onboarding-and-config.md`
- Affected code: `src/Netclaw.Actors/Sessions/LlmSessionActor.cs`, `src/Netclaw.Actors/Channels/ChannelPipeline.cs`, `src/Netclaw.Actors/Channels/ISessionLifecycleObserver.cs`, `src/Netclaw.Daemon/Gateway/SessionCatalogService.cs`, related tests.
- API/runtime impact: `GET /api/sessions` continues returning session status, but inactive rows use `inactive` and keep their prior `last_activity` until real activity occurs.
- Security/operations: no ACL or grant changes; improves operator visibility by making active-session counts match live actor availability.
- Out of scope: graceful drain/tombstone work for config-driven restart in issue `#326`.
