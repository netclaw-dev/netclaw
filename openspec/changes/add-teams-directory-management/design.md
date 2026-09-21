## Context

See `proposal.md` for motivation. Teams already has an SDK-free channel project,
daemon-only Teams SDK translation, fail-closed tenant/team/channel/audience
checks, and durable personal/channel binding actors. The native Channels TUI
currently supports Slack, Discord, and Mattermost, while Teams configuration is
file-only. Its existing `AllowedUserIds` check is synchronous and is repeated at
the ingress router, durable conversation, and binding boundaries.

## Goals / Non-Goals

**Goals:**

- Add a narrow SDK-free directory contract for canonical Teams users, groups,
  teams, and channels; keep Microsoft Graph types in an infrastructure project.
- Reuse the configured Teams client-secret identity for app-only Graph access;
  validate Graph availability and capabilities without persisting a token.
- Preserve legacy Teams behavior exactly when no group restriction is present,
  while applying a consistent default-deny principal union when it is present.
- Give the Channels TUI safe first-connect, ongoing configuration, bounded
  directory search, and informative non-secret diagnostics.

**Non-Goals:**

- No Microsoft Graph history hydration, channel membership authorization,
  directory write, `Directory.Read.All`, or tenant-wide directory export.
- No Azure/Entra provisioning, manifest change, deployment, live tenant test,
  live approval callback Graph request, or generic approval/session rewrite.
- No Teams SDK 2.1 adapter/translator/endpoint, persistent schema, proactive
  delivery, reply, thread, or personal-routing replacement.

## Decisions

### Keep an SDK-free contract and a daemon-owned Graph implementation

`Netclaw.Channels.Teams` defines immutable directory records and a narrow
`ITeamsDirectory` boundary: search teams/channels/users/groups, get a user, and
check a user against candidate group IDs. `Netclaw.Channels.Teams.Graph` owns
`GraphServiceClient`, `ClientSecretCredential`, response translation, retry,
timeouts, and `IMemoryCache`. The daemon composes the one long-lived Graph
client and implementation only when a complete Teams credential is available.
CLI code consumes the SDK-free boundary through a small factory/probe; it never
receives a Graph SDK object.

This is preferred over adding Graph calls to the daemon translator or policy
classes, which would leak vendor types and make the policy test-only boundary
impossible to fake narrowly.

### Reuse the existing credential with explicit least privilege

The Graph client uses `ClientSecretCredential(TenantId, ClientId,
ClientSecret)` and only the `.default` scope
`https://graph.microsoft.com/.default`. Operator documentation requires
admin-consented application permissions: `Team.ReadBasic.All`,
`Channel.ReadBasic.All`, `User.Read.All`, and `GroupMember.Read.All`; it
explicitly prohibits `Directory.Read.All`. The secret remains in the existing
secrets overlay/environment, is never put in normal config/schema, cache keys,
doctor output, UI summaries, or logs.

### Bound every directory operation

The Graph service applies a five-second operation deadline, caller
cancellation, a limited retry only for retryable transient failures, and honors
server retry-after values within that deadline. Query length and result pages
are bounded; directory search is server-filtered/search-based and never
enumerates the tenant. A cache-aside `IMemoryCache` stores profile and
membership results for ten minutes, team/channel/group records for thirty
minutes, and search results for at most five minutes. Keys contain only tenant,
resource type, canonical ID, or a hashed bounded query. Cache size limits are
set so input churn cannot grow memory without bound.

### Evaluate authorization as a staged, async policy

The existing tenant, scope, team/channel, root, mention, and audience gates run
first. A Teams principal authorizer then evaluates users before groups:

1. A configured global or matching per-channel explicit user immediately
   allows without a Graph request.
2. If a relevant group restriction exists, the authorizer checks the union of
   applicable group IDs by `POST /users/{id}/checkMemberGroups`, de-duplicated
   and chunked at no more than 20 IDs; it returns on the first positive chunk.
3. If a group restriction applies but no candidate matches, it denies with
   `teams_group_membership_not_allowed`. If Graph is unavailable, malformed,
   unauthorized, throttled after bounded retry, or times out, it denies with
   `teams_group_membership_unavailable`.

For a channel, a configured principal restriction is the union of global user
and group lists and the matching structured channel override's user/group
lists. With no applicable restriction, legacy channel behavior is unchanged.
For a direct message, `AllowDirectMessages` plus a global explicit user or a
global group match is required; per-channel overrides never authorize DMs and
empty global lists deny. Only explicit users or verified group membership may
produce `TrustedInternal`; team/channel membership alone never does.

The host routes after awaiting authorization and passes its decision into the
actor ingress contract. Actors retain their defensive legacy structural checks,
but do not issue Graph calls or block on async work. This prevents network I/O
on Akka dispatchers and ensures that a decision used to create a turn is the
decision passed to its binding.

### Persist configuration as canonical IDs and structured overrides

`TeamsChannelOptions` gains additive `AllowedGroupIds` and
`TeamsChannelAccessOverride` entries containing TeamId, ChannelId,
AllowedUserIds, and AllowedGroupIds. Structured overrides avoid concatenated
or delimiter-dependent identifiers. Existing Teams arrays and
`ChannelAudienceOverrides` remain readable and retain their precedence rules.
The TUI saves only canonical IDs; it can resolve friendly labels through the
cache when reopened and falls back to an abbreviated ID rather than deleting or
rewriting an unresolved entry.

### Treat TUI directory lookup as interactive, not bulk administration

Teams becomes a first-class picker item after Mattermost. First connect asks
for Tenant ID, Application Client ID, Bot ID, and a masked Client Secret; blank
secret input on edit preserves the stored value and explicit rotation replaces
it. The management home shows configured counts and actions for channels,
users, groups, DMs, directory status, credentials, enable/disable, and reset.
Search requires a minimum input length, debounces 250–350ms, cancels superseded
queries, limits results to 25–50, and never blocks via `.Result` or `.Wait`.
The normal channel flow selects a team then a channel then audience; an
advanced canonical-ID path remains available for recovery.

### Keep approvals on their established transport path

Graph may enrich an approval presenter label only from an already-cached user
record. `Action.Execute` performs no directory lookup. Its displayed operator
name remains callback display name, then cached `Display Name <UPN>`/UPN, then
the exact existing `Authorized operator` fallback—never a canonical ID.

## Risks / Trade-offs

- [Consent is incomplete or Graph is unavailable] → configuration diagnostics
  explain the missing capability without secrets; group-constrained ingress
  fails closed while explicit-user-only access continues without Graph.
- [Membership is slow or throttled] → five-second deadline, cancellation,
  bounded retry, short membership cache, early positive result, and safe reason
  code prevent back-pressure or accidental allow.
- [Teams activity needs revalidation after durable routing] → the host carries
  the completed principal decision rather than issuing Graph I/O inside actors.
- [Directory labels later disappear] → canonical saved IDs remain and the TUI
  renders a safe abbreviation instead of deleting access configuration.
- [Graph SDK surface changes] → only the Graph project adapts it; the contract,
  policies, tests, TUI, and daemon composition remain SDK-independent.

## Migration Plan

1. Ship additive configuration with empty group lists and no access overrides;
   existing Teams behavior remains unchanged.
2. Document consent, restart, and the TUI workflow. Operators opt in by adding
   canonical groups or per-channel principals after Graph validation succeeds.
3. If rollback is required, remove the new binary or leave new lists empty.
   Existing Teams configuration and persistence are unchanged; group-restricted
   installs should remove group restrictions before a binary rollback to avoid
   an unintended broadening of access.
