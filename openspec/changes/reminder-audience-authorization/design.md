## Context

Reminder definitions are minted through multiple entry points today: the conversational `set_reminder` tool, daemon REST endpoints, CLI/admin flows that call those endpoints, and raw import of serialized reminder definitions. The persisted `ReminderDefinition.Audience` field is then consumed by `ReminderExecutionActor`, which currently falls back to deployment posture defaults when the field is null.

That behavior is too permissive for PRD-002 and PRD-008. The authority that created a reminder is session-scoped and may be narrower than the deployment default. If reminder minting does not validate audience ordering, a low-authority source can create or import a reminder that later executes with a broader audience than the source that requested it.

This is a cross-cutting security correction because it touches reminder persistence, tool-driven creation, admin/import entry points, and execution assumptions. The design needs one authoritative mint-time validation rule rather than per-surface ad hoc checks.

## Goals / Non-Goals

**Goals:**

- Define one server-side reminder minting rule: stored reminder audience MUST be less than or equal to the creator's current source audience / authority
- Make omitted `audience` inherit from the creating session/channel for conversational and tool-created reminders
- Allow creators to intentionally lower reminder audience (for example Personal -> Team or Public)
- Ensure REST/admin/CLI/import paths reject invalid or over-privileged reminder definitions before persistence
- Clarify that execution may trust the stored audience because minting validation guarantees the invariant

**Non-Goals:**

- Changing the `TrustAudience` ordering or introducing new audience kinds
- Revalidating creator authority on every reminder execution
- Broad ACL redesign beyond reminder minting and import/update paths
- Adding client-only validation as a substitute for server-side enforcement

## Decisions

### D1: Enforce reminder audience at mint time in the reminder manager path

**Decision**: all reminder write surfaces shall flow through a single validation step in the reminder save path, with the caller supplying the source authority context used for comparison.

**Rationale**: `ReminderManagerActor.HandleSaveAsync` is already the convergence point for creation and replacement writes. Putting the invariant there keeps REST import, CLI/admin calls, and the LLM tool on the same rule set and prevents weaker paths from bypassing validation.

**Alternative considered**: validate independently in the REST endpoint, tool, and CLI. Rejected because duplicated checks drift easily and import paths can still bypass them.

### D2: Omitted conversational audience inherits session/channel audience, not deployment posture

**Decision**: when a reminder is created from a conversation or tool context and `audience` is omitted, the server persists the effective audience from the creating session/channel. Null is no longer treated as "use deployment default later" for conversational minting.

**Rationale**: the session that requested the reminder is the actual trust source. Persisting that resolved audience preserves the creator's authority boundary across restarts and future execution.

**Alternative considered**: keep storing null and reinterpret null at execution time based on session metadata or deployment posture. Rejected because it leaves execution coupled to mutable defaults and obscures the minted authority.

### D3: Non-conversational write paths must provide an explicit creator ceiling

**Decision**: REST/admin/CLI/import writes must be accompanied by a server-side authority ceiling derived from the authenticated caller or explicit import context. If that ceiling is unavailable, the write fails closed rather than assuming deployment defaults.

**Rationale**: server-side validation is only meaningful if the server knows whose authority the reminder is being minted under. Import is especially sensitive because it can carry serialized `Audience` values from outside the process.

**Alternative considered**: treat admin/import as implicitly Personal. Rejected because it silently upgrades privilege and contradicts default-deny.

### D4: Lowering audience is always allowed; raising above source authority is never allowed

**Decision**: audience comparison uses existing `TrustAudience` ordering (`Public < Team < Personal`). A requested audience equal to or narrower than the source audience is accepted. A broader audience is rejected with a validation error that identifies both values.

**Rationale**: this matches the existing trust model in `SecurityPolicyDefaults` and makes reminder minting monotonic in the safe direction.

**Alternative considered**: only allow exact audience equality. Rejected because operators need to intentionally down-scope reminders for safer execution.

### D5: Execution trusts stored audience after successful minting

**Decision**: `ReminderExecutionActor` uses the stored reminder audience directly. Execution does not recompute audience from deployment posture when the reminder came from a validated mint path.

**Rationale**: execution should be simple and deterministic. The secure place to reject bad authority is before persistence, not on a future timer tick where the original source context may be unavailable.

**Alternative considered**: keep execution fallback logic and re-check against current defaults. Rejected because it permits behavior drift after creation and weakens the auditability of stored reminder definitions.

## Risks / Trade-offs

- [Legacy reminders may still have null or over-broad audiences on disk] -> Mitigation: scope this change to newly created/imported reminders; implementation can treat legacy data explicitly and fail clearly if later write/update paths encounter invalid state rather than silently broadening.
- [Some admin/import callers may not yet propagate source authority context] -> Mitigation: make missing authority context an explicit validation failure so each write surface is forced onto the shared contract.
- [Persisting inherited conversational audience changes previously observed behavior] -> Mitigation: this is the intended security correction and should be covered by tests and clear validation/error messaging.
- [Replace/upsert of an existing reminder can become invalid under a narrower current caller] -> Mitigation: validate every write using the current caller's authority, not the reminder's historical creator.

## Migration Plan

1. Add a reminder audience validation helper/contract in the save path and thread source authority through all write callers.
2. Update conversational minting to resolve omitted audience from session/channel context and persist that value.
3. Update REST/admin/import surfaces to reject missing, invalid, or over-privileged audience values before persistence.
4. Update execution tests and API/tool messaging to reflect that stored reminder audience is trusted and omitted conversational audience no longer means deployment default.

Rollback is straightforward: revert the validation and inheritance changes. No schema migration is required because the persisted field already exists.

## Open Questions

1. For authenticated REST/admin callers that do not originate from a chat session, what exact server-side source establishes the authority ceiling today: authenticated principal classification, explicit request metadata, or a management-default audience? The implementation should use the narrowest existing authoritative source rather than inventing a new implicit default.
