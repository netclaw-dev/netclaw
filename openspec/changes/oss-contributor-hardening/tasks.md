## 1. Phase 0 Compatibility Safety Nets

- [ ] 1.1 Identify and document the current protected regression paths for OpenAI API-key inference, OpenAI OAuth/subscription runtime, and Slack Socket Mode thread routing/reply delivery.
- [ ] 1.2 Add contract tests that protect OpenAI API-key runtime client construction and inference behavior without requiring live provider credentials.
- [ ] 1.3 Add contract tests that protect OpenAI OAuth/subscription token-to-runtime-client behavior and auth failure reporting without requiring live provider credentials.
- [ ] 1.4 Add scenario tests that protect Slack Socket Mode connection, `{channelId}/{threadTs}` session routing, and in-thread reply delivery behavior.
- [ ] 1.5 Add validation scenario coverage for unknown provider kinds, unknown channel kinds, invalid auth state, and invalid notification targets asserting fail-closed behavior with no silent fallback.

## 2. Shared Seam Types And Invariants

- [ ] 2.1 Introduce shared value objects for provider identifiers, channel kinds, notification target kinds, and related seam keys at generic boundaries.
- [ ] 2.2 Replace free-form seam strings in shared runtime contracts and config-binding boundaries with the new value objects using explicit conversions only.
- [ ] 2.3 Inventory persistence and serialization boundaries touched by provider/channel/notification identifiers and update serialization behavior to preserve compatibility.
- [ ] 2.4 Normalize validation vocabulary and invariant categories so schema, doctor, startup, and hot reload report the same seam failures.

## 3. Provider Module Seam Extraction

- [ ] 3.1 Introduce a single compiled-in provider module registry for provider selection, validation, model discovery, and runtime client construction.
- [ ] 3.2 Move existing provider registration into the compiled-in provider module seam without changing actor-facing runtime contracts.
- [ ] 3.3 Update provider configuration binding and startup composition to resolve providers exclusively through the compiled-in registry.
- [ ] 3.4 Add contributor-facing validation errors for unknown provider kinds and explicitly reject dynamic provider plugin loading.
- [ ] 3.5 Run the protected OpenAI API-key and OAuth/subscription regression coverage after provider seam extraction and fix any compatibility drift before continuing.

## 4. Provider Auth And OAuth Seam Extraction

- [ ] 4.1 Introduce explicit provider-auth lifecycle seams for token acquisition, token refresh, token persistence, and token-to-runtime-client mapping.
- [ ] 4.2 Extract OpenAI API-key authentication onto the provider-auth seam while preserving current successful runtime behavior.
- [ ] 4.3 Extract OpenAI OAuth/subscription authentication onto the provider-auth seam while preserving current successful runtime behavior.
- [ ] 4.4 Update doctor and startup validation to report provider-auth stage-specific failures for incomplete or invalid auth configurations.
- [ ] 4.5 Ensure auth refresh/runtime failures fail loudly and do not silently downgrade to another auth mode or stale fallback client.

## 5. Channel Module Seam Extraction

- [ ] 5.1 Introduce a single compiled-in channel module registry for inbound adapters, outbound delivery, and channel registration.
- [ ] 5.2 Move Slack registration behind the compiled-in channel module seam without changing `SendUserMessage` or broadcast-based actor contracts.
- [ ] 5.3 Update channel composition and validation to resolve configured channels exclusively through the compiled-in registry.
- [ ] 5.4 Add contributor-facing validation errors for unknown channel kinds and explicitly reject dynamic channel plugin loading.
- [ ] 5.5 Run the protected Slack runtime regression coverage after channel seam extraction and fix any Socket Mode, thread routing, or reply-delivery drift before continuing.

## 6. Runtime Notification, Webhook, And Reminder Decoupling

- [ ] 6.1 Inventory Slack-only assumptions in reminder notifications, inbound webhook flows, operational alerts, and other runtime notification producers.
- [ ] 6.2 Introduce a generic runtime notification contract using typed notification targets and channel kinds.
- [ ] 6.3 Refactor reminder, webhook, and operational alert producers to emit generic runtime notifications instead of Slack-specific target kinds or Slack-only tool names.
- [ ] 6.4 Route runtime notification delivery through the compiled-in channel module seam while preserving Slack as the first successful delivery implementation.
- [ ] 6.5 Add fail-closed behavior for unknown notification target kinds or channel kinds with no silent reroute to Slack or any other channel.

## 7. Schema And Validation Alignment

- [ ] 7.1 Update the configuration schema for provider, channel, auth, and notification seam changes so unknown or partial seam definitions are rejected structurally.
- [ ] 7.2 Update `netclaw doctor` seam diagnostics to distinguish provider, channel, auth, and notification invariant failures with actionable remediation.
- [ ] 7.3 Update startup validation to enforce the same seam invariants as schema and doctor before runtime activation.
- [ ] 7.4 Update hot-reload validation to reject invalid provider, channel, auth, and notification changes and retain the last valid runtime state.
- [ ] 7.5 Add regression coverage proving schema, doctor, startup, and hot reload agree on the same invalid seam cases and never apply silent fallbacks.

## 8. Test Consolidation And Cleanup

- [ ] 8.1 Identify seam-local narrow tests that no longer provide meaningful protection once contract and scenario coverage exists.
- [ ] 8.2 Remove or consolidate low-value seam-local tests in favor of smaller high-value contract suites around provider, channel, auth, and notification seams.
- [ ] 8.3 Add broader scenario coverage that exercises compatibility-critical user-visible flows across the extracted seams.
- [ ] 8.4 Verify required CI remains contributor-safe and secret-free while optional live smoke checks remain explicit opt-in workflows.

## 9. Verification And Exit Checks

- [ ] 9.1 Re-run the protected OpenAI API-key, OpenAI OAuth/subscription, and Slack runtime regression suites after all seam changes are integrated.
- [ ] 9.2 Run targeted validation coverage for unknown provider kinds, unknown channel kinds, invalid auth configurations, and invalid notification targets to confirm fail-closed behavior.
- [ ] 9.3 Run `dotnet slopwatch analyze` and fix any new violations introduced by the hardening work.
- [ ] 9.4 Confirm no dynamic plugin loading paths, no silent fallbacks, and no actor-contract regressions remain in the final implementation.
- [ ] 9.5 Verify the OpenSpec task list, delta specs, and implementation outcomes stay aligned before marking the change ready for archive or apply completion.
