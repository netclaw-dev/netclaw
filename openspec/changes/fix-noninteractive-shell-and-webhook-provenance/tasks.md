## 1. Unify shell path validation (Part A — closes #1244)

- [x] 1.1 Change `IShellTrustZonePolicy` from `GetTrustZoneRoots(context)` to a write-path authorization method (e.g. `bool IsShellWritePathAuthorized(string fullPath, ToolExecutionContext context)`).
- [x] 1.2 Update `ShellTrustZonePolicy` to delegate the new method to the wrapped `ScopedFileAccessPolicy.TryResolveWritePath` (Mode.All ⇒ allow, Mode.Roots ⇒ confine, Mode.None ⇒ deny, incl. symlink defense).
- [x] 1.3 Rewrite `ToolAccessPolicy.EnforceShellTrustZones` to call the new policy method per extracted path token and for the working directory; keep `ExtractShellPathTokens` and `ShellTokenizer.NormalizePathToken(token, workingDirectory)`; deny with `shell_path_outside_trust_zone` / `shell_working_directory_outside_trust_zone`. Remove the empty-roots `shell_no_trust_zone_roots` path.
- [x] 1.4 Leave the `_shellTrustZonePolicy is null` fail-closed branch, the hard-deny pre-check, and the `ToolPathPolicy` protected-path pre-check unchanged.

## 2. Webhook audience provenance (Part B)

- [x] 2.1 `SetWebhookTool`: override the context-aware `ExecuteAsync(Params, ToolExecutionContext, ct)`; resolve audience as `requested ?? context.Audience` (inherit when omitted instead of defaulting to `Public`); update the `Audience` parameter description to "omit to inherit the creating session/channel audience".
- [x] 2.2 Add a downgrade-only escalation guard in `SetWebhookTool.TryResolveAudience` (the agent-creation boundary — webhooks have no manager actor), mirroring `ReminderManagerActor.ValidateRequestedAudience` (reject requested audience greater than creator authority).
- [x] 2.3 Keep `WebhooksConfig.Audience = Public` as the file-defined (no-creator-context) default; confirmed no `*Config` shape change (so no `netclaw-config.v1.schema.json` update).

## 3. Autonomous filesystem zone (defensive clamp)

- [ ] 3.1 Add `ToolConfig.AutonomousZoneRoots` (token-aware list; `{session_dir}` implicit, `{project_dir}`, `{workspaces_dir}`, literals) and update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` (Config Schema Sync Rule).
- [ ] 3.2 Add `ToolAudienceProfileResolver.ResolveAutonomousRoots(context)` resolving the configured roots (reuse the existing `ResolveToken` machinery; add a `{project_dir}` token sourced from `context.ProjectDirectory`).
- [ ] 3.3 Apply the clamp at the single seam — `ScopedFileAccessPolicy.TryResolvePath`'s `Mode.All` short-circuit: for `context.SupportsInteractiveApproval == false`, confine to the autonomous zone (`session_dir` + `project_dir` + configured roots); `Mode.All` → zone, `Mode.Roots` → audience roots ∩ zone, `Mode.None` → deny; empty zone fails closed. Interactive path unchanged.
- [ ] 3.4 Verify the clamp covers shell (via `TryResolveWritePath`) and all file tools (`file_read`/`file_write`/`file_edit`/`file_list`/`attach_file`) through the shared `TryResolvePath` seam — no per-tool special-casing.
- [ ] 3.5 Tests: autonomous Personal denied outside zone (shell AND file_read), permitted inside zone; clamp never widens autonomous Public (stays session-scoped); interactive Personal still `Mode.All` unrestricted; operator `AutonomousZoneRoots` extends the zone.

## 4. Tests (Parts A & B)

- [x] 4.1 Update `ToolApprovalGateTests` trust-zone tests to the unified semantics; `FakeShellTrustZonePolicy` now implements `IsShellWritePathAuthorized` via `IsWithinAnyRoot` (Mode.Roots simulation). Out-of-root → `shell_path_outside_trust_zone`; out-of-root working directory → `shell_working_directory_outside_trust_zone`.
- [x] 4.2 Add `Non_interactive_shell_with_unrestricted_audience_proceeds_to_approval` — real `ShellTrustZonePolicy` with the Personal (Mode.All) profile → no `shell_no_trust_zone_roots`, proceeds to the approval gate (RequiresApproval). (#1244 regression.)
- [x] 4.3 Add `SetWebhookToolProvenanceTests`: omitted audience inherits creator (Personal/Team/Public); explicit downgrade allowed; upscope rejected and not persisted.

## 5. Compliance & verification

- [x] 5.1 Update the `netclaw-operations` system skill (`set_webhook` audience inheritance; correct the stale "trust zone path" notes for reminders/webhooks) and bump `metadata.version` to 2.8.4; manifest not regenerated locally. (Re-touch when the autonomous zone lands — the reminder/webhook path guidance now points at the zone.)
- [ ] 5.2 Eval suite: no existing case references webhooks; running `./evals/run-evals.sh` requires a Docker image + live model-provider endpoint (unavailable in this environment). Deferred to CI / pre-merge.
- [x] 5.3 `dotnet build Netclaw.slnx` clean; `dotnet test Netclaw.Actors.Tests` (2130 passed); `dotnet slopwatch analyze` (0 issues); `./scripts/Add-FileHeaders.ps1 -Verify` (all headers present). (Re-run after task 3.)
- [ ] 5.4 End-to-end CLI repro (`netclaw chat -p` / live webhook) requires a built CLI + model provider; behavior is unit-verified by tasks 4.2/4.3 and (for the zone) 3.5. Run pre-merge with a provider configured.
- [ ] 5.5 OpenSpec: artifacts complete + `openspec validate` passes; spec scenarios covered by tasks 3.5 / 4.1–4.3. Formal `/opsx-verify`, `/opsx-sync`, `/opsx-archive` deferred until after PR merge.
