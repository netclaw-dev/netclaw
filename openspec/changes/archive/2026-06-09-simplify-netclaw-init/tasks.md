## 1. OpenSpec planning artifacts and traceability

- [x] 1.1 Remove all planning references to `netclaw init --force`.
- [x] 1.2 Confirm the artifacts reflect bootstrap-only init and init-owned
  Identity.
- [x] 1.3 Run `openspec validate simplify-netclaw-init --type change`.

## 2. First-run bootstrap flow

- [x] 2.1 Trim init to the bootstrap steps only.
- [x] 2.2 Keep posture values to `Personal`, `Team`, `Public`.
- [x] 2.3 Keep Security Posture, Enabled Features, and Audience Profiles
  distinct in planning and implementation.
- [x] 2.4 When posture is `Personal`, skip Enabled Features.
- [x] 2.5 When posture is `Team` or `Public`, automatically continue into
  Enabled Features.

## 3. Existing-install init menu

- [x] 3.1 Detect an existing install before entering the first-run flow.
- [x] 3.2 Show exactly these existing-install options:
  `Redo identity setup`, `Open configuration editor`,
  `Start over from scratch`, `Cancel`.
- [x] 3.3 Route `Open configuration editor` to `netclaw config`.
- [x] 3.4 Route `Redo identity setup` into the init-owned identity flow.

## 4. Start-over flow

- [x] 4.1 Implement the `Start over from scratch` dialog with exactly:
  `Reset setup only`, `Full reset`, `Cancel`.
- [x] 4.2 Require double confirmation before either destructive action.
- [x] 4.3 Remove all implementation planning tied to `--force` backup or
  flag parsing.

## 5. Identity ownership

- [x] 5.1 Keep Identity owned by init.
- [x] 5.2 Remove any planning language that assumes Identity moves into
  `netclaw config`.

## 6. Post-flight messaging

- [x] 6.1 Point successful bootstrap users to `netclaw chat` and
  `netclaw config`.
- [x] 6.2 Keep messaging consistent with the bootstrap-vs-config split.

## 7. Coverage

- [x] 7.1 Rewrite init smoke coverage for the bootstrap-first flow.
- [x] 7.2 Add coverage for the existing-install action menu.
- [x] 7.3 Add coverage for the start-over dialog and double confirmation.
- [x] 7.4 Remove old smoke planning tied to `init --force`.

## 8. Quality gates

- [x] 8.1 `dotnet build` clean.
- [x] 8.2 `dotnet test` clean.
- [x] 8.3 `./scripts/smoke/run-smoke.sh init-wizard` clean.
- [x] 8.4 `./scripts/smoke/run-smoke.sh light` clean.
- [x] 8.5 `dotnet slopwatch analyze` clean.
- [x] 8.6 `./scripts/Add-FileHeaders.ps1 -Verify` clean.
- [x] 8.7 `openspec validate simplify-netclaw-init --type change`
  passes.
