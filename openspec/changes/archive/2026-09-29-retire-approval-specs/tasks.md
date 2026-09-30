## 1. Archive the in-flight approval changes

- [x] 1.1 Check each change whose delta targets a retired capability: tasks, code behavior, and sync state.
- [x] 1.2 Sync their deltas for kept capabilities and archive them without the retired deltas.
- [x] 1.3 Point links to archived change folders at the stable evidence path or the archive path.

## 2. Move and enrich

- [x] 2.1 Move the ingress requirements of `netclaw-gateway-security` to their specs.
- [x] 2.2 Move the Slack resolution line to `netclaw-slack-socket`.
- [x] 2.3 Fold `skill-trust-tiers` into `skill-tools` with the `.system` collision rule.
- [x] 2.4 Add the missing details to TA-1, TA-3, TA-6, and TA-10.

## 3. Retire and trim

- [x] 3.1 Remove every requirement of the five approval capabilities and `skill-trust-tiers`, each with a reason.
- [x] 3.2 Trim the authorization requirements from the other specs.
- [x] 3.3 Correct the default shell timeout in `netclaw-tools`.

## 4. Links and verification

- [x] 4.1 Update `TOOLING.md` and `openspec/specs/README.md`.
- [x] 4.2 Run `openspec validate --specs --strict` and `openspec validate retire-approval-specs --strict`.
- [x] 4.3 Check the relative links in changed Markdown.
