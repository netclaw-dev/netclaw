## 1. OpenSpec planning artifacts and traceability

- [ ] 1.1 Remove all planning references to `netclaw init --force`.
- [ ] 1.2 Confirm the artifacts reflect bootstrap-only init and init-owned
  Identity.
- [ ] 1.3 Run `openspec validate simplify-netclaw-init --type change`.

## 2. First-run bootstrap flow

- [ ] 2.1 Trim init to the bootstrap steps only.
- [ ] 2.2 Keep posture values to `Personal`, `Team`, `Public`.
- [ ] 2.3 Keep Security Posture, Enabled Features, and Audience Profiles
  distinct in planning and implementation.
- [ ] 2.4 When posture is `Personal`, skip Enabled Features.
- [ ] 2.5 When posture is `Team` or `Public`, automatically continue into
  Enabled Features.

## 3. Existing-install init menu

- [ ] 3.1 Detect an existing install before entering the first-run flow.
- [ ] 3.2 Show exactly these existing-install options:
  `Redo identity setup`, `Open configuration editor`,
  `Start over from scratch`, `Cancel`.
- [ ] 3.3 Route `Open configuration editor` to `netclaw config`.
- [ ] 3.4 Route `Redo identity setup` into the init-owned identity flow.

## 4. Start-over flow

- [ ] 4.1 Implement the `Start over from scratch` dialog with exactly:
  `Reset setup only`, `Full reset`, `Cancel`.
- [ ] 4.2 Require double confirmation before either destructive action.
- [ ] 4.3 Remove all implementation planning tied to `--force` backup or
  flag parsing.

## 5. Identity ownership

- [ ] 5.1 Keep Identity owned by init.
- [ ] 5.2 Remove any planning language that assumes Identity moves into
  `netclaw config`.

## 6. Post-flight messaging

- [ ] 6.1 Point successful bootstrap users to `netclaw chat` and
  `netclaw config`.
- [ ] 6.2 Keep messaging consistent with the bootstrap-vs-config split.

## 7. Coverage

- [ ] 7.1 Rewrite init smoke coverage for the bootstrap-first flow.
- [ ] 7.2 Add coverage for the existing-install action menu.
- [ ] 7.3 Add coverage for the start-over dialog and double confirmation.
- [ ] 7.4 Remove old smoke planning tied to `init --force`.

## 8. Quality gates

- [ ] 8.1 `dotnet build` clean.
- [ ] 8.2 `dotnet test` clean.
- [ ] 8.3 `./scripts/smoke/run-smoke.sh init-wizard` clean.
- [ ] 8.4 `./scripts/smoke/run-smoke.sh light` clean.
- [ ] 8.5 `dotnet slopwatch analyze` clean.
- [ ] 8.6 `./scripts/Add-FileHeaders.ps1 -Verify` clean.
- [ ] 8.7 `openspec validate simplify-netclaw-init --type change`
  passes.
