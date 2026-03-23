## 1. Trust context and policy model

- [x] 1.1 Add trust-context, audience, provenance, and shell-mode configuration types with strict-default semantics in `Netclaw.Configuration`.
- [x] 1.2 Extend inbound source/session metadata so adapters can carry audience, principal classification, and verified-source vs payload-taint fields.
- [x] 1.3 Implement runtime trust-context derivation that composes deployment posture, source metadata, and working-context downgrades into an effective audience.

## 2. ACL, memory, and tool enforcement

- [x] 2.1 Update ACL evaluation so admission returns both allow/deny and initial trust-context/audience decisions.
- [x] 2.2 Extend durable memory policy with audience-aware filtering and conservative migration/default rules for existing memory rows.
- [x] 2.2.1 Add runtime-owned memory security boundaries so reusable project/entity memories can cross channels inside the same authorized boundary without depending on channel-derived domains.
- [x] 2.2.2 Add secret-content detection/redaction to memory formation so raw credentials, keys, and tokens are never persisted as durable memory.
- [x] 2.3 Update built-in tool exposure and invocation policy to honor effective trust context, capability classes, and shell mode.
  - [x] Restrict `file_read` and `file_write` to the current session directory when the active trust context is `public`.
  - [x] Replace hardcoded file/tool scope heuristics with audience-scoped policy profiles that define explicit tool visibility and filesystem roots.
- [x] 2.4 Update MCP server policy handling to support capability classification and trust-context-aware exposure/invocation.
  - [x] Add server-level audience-profile allow/deny handling for MCP discovery and invocation so broader personal settings do not leak into `team` or `public` contexts.

## 3. Diagnostics and configuration UX

- [x] 3.1 Update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` and related option binding for posture, audience-scoped profiles, shell mode, filesystem scopes, unrestricted `personal` modes, and MCP capability metadata.
- [x] 3.2 Add `netclaw doctor` checks and explain output for strict defaults, unsafe combinations, and missing trust-policy metadata.
- [x] 3.3 Update onboarding/init flows to derive recommended security posture defaults without requiring full manual policy authoring.
  - [x] Generate recommended resolved `public`, `team`, and `personal` profiles during onboarding and explain how operators can edit them.

## 4. Validation and follow-up planning

- [x] 4.1 Add tests covering trust-context derivation, downgrade-only transitions, audience-aware memory filtering, and tool/MCP denial behavior.
- [x] 4.2 Update repository docs/spec references affected by the new trust-context model and operator-facing diagnostics.
- [x] 4.3 File or link a follow-up issue/change for sandboxed/isolated execution so `sandbox-only` shell mode has an implementation path after this planning slice.
  - [ ] Decide whether flattened audience profiles remain the long-term operator model or whether a later shortcut/inheritance format needs a separate change.
