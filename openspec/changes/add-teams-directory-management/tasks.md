## 1. Directory and configuration foundations

- [ ] 1.1 Add SDK-free Teams directory records, outcomes, and the narrow directory boundary; verify contract tests cover canonical records and safe unavailable outcomes.
- [ ] 1.2 Add additive global group IDs and delimiter-safe `TeamsChannelAccessOverride` configuration; update schema/validation and verify legacy Teams configuration still binds unchanged.
- [ ] 1.3 Add the Teams Graph infrastructure project, solution wiring, and current supported Graph/Azure Identity dependencies through the package CLI; verify restore and project-reference containment.
- [ ] 1.4 Implement a long-lived Graph client using the existing Teams app credential and `.default` scope; verify tests prove no Graph SDK types cross the Teams contract boundary and no second secret/token is persisted.

## 2. Bounded Microsoft Graph directory service

- [ ] 2.1 Implement bounded team/channel/user/group lookup and profile projection with canonical IDs and capped result pages; verify fake-boundary tests cover lookup, invalid input, and no tenant enumeration.
- [ ] 2.2 Implement bounded cache-aside storage with tenant-scoped non-secret cache keys, required TTLs, and size limits; verify expiry, key isolation, and cache-hit behavior.
- [ ] 2.3 Implement cancellation, five-second operation timeout, and bounded retry honoring retry-after; verify timeout, cancellation, malformed response, unauthorized, and throttling tests.
- [ ] 2.4 Implement deduplicated, chunked-at-20 `checkMemberGroups` verification with early positive completion; verify exact chunks, no duplicate IDs, cache behavior, and all safe unavailable outcomes.
- [ ] 2.5 Wire Graph availability/capability diagnostics without secret output; verify complete, incomplete, permission-denied, and disabled states through focused tests.

## 3. Principal authorization and runtime integration

- [ ] 3.1 Implement principal selection that combines global and exact structured channel user/group restrictions while preserving legacy unrestricted-channel behavior; verify policy matrix tests.
- [ ] 3.2 Implement explicit-user Graph bypass, matching-group authorization, stable no-match and unavailable deny reasons, and trusted-internal classification; verify no raw IDs occur in stable reasons.
- [ ] 3.3 Apply direct-message authorization using global principals only, with empty lists denied and channel overrides ignored; verify DM matrix tests.
- [ ] 3.4 Integrate completed async authorization at the Teams ingress/actor boundary without Graph I/O or blocking waits in actors; verify all durable routing and continuation paths retain existing tenant/channel/root/mention protections.
- [ ] 3.5 Use only cached directory identity enrichment for approval presentation and preserve callback-name then `Authorized operator` fallback; verify approval callbacks make no directory request and retain existing replay/callback behavior.

## 4. Native Teams configuration experience

- [ ] 4.1 Add Teams after Mattermost to the Channels picker, draft mapper, summaries, enable/disable, reset, and initial setup; verify existing Slack, Discord, and Mattermost configuration tests remain unchanged.
- [ ] 4.2 Implement masked first-connect and explicit credential rotation with blank-secret preservation and existing secrets-overlay persistence; verify no normal-config, rendered summary, or log includes the client secret.
- [ ] 4.3 Add the Teams management home with channel/user/group/DM counts, directory status, and save lifecycle; verify management action tests cover all navigation and autosave/reset behavior.
- [ ] 4.4 Implement debounced, cancellable, bounded asynchronous directory search and team-then-channel configuration with canonical-ID persistence plus advanced-ID fallback; verify stale search results cannot update the view and unresolved saved IDs are retained.
- [ ] 4.5 Add global and per-channel user/group editing and safe label refresh; verify structured overrides are stable, friendly labels never become authority, and no edit broadens access accidentally.

## 5. Documentation, safety, and validation

- [ ] 5.1 Update Teams setup/runbook documentation with the exact least-privilege Graph consent, secret boundary, cache/failure behavior, configuration examples, and no-live-test limitation; verify no `Directory.Read.All` requirement is introduced.
- [ ] 5.2 Add Teams doctor and schema/fix coverage for configuration, directory capability, and safe diagnostics; verify doctor messages contain neither client secrets, tokens, nor raw principal IDs.
- [ ] 5.3 Run focused Graph/cache/ACL/TUI/Teams posting, personal routing, and approval regression suites; record exact results in this change after all pass.
- [ ] 5.4 Run restore, solution build, full test suite, package vulnerability check, Slopwatch, header/format/whitespace checks, strict OpenSpec validation, and `git diff --check`; record exact results and any pre-existing scoped findings.
- [ ] 5.5 Inspect the final diff for cross-channel, generic approval, persistence, SDK 2.1 adapter, and secret-handling containment; provide a no-live-tenant owner test plan before creating a bot-authored PR.
