## Context

See `proposal.md` for motivation. Teams receives `adaptiveCard/action` invokes
through the Core SDK pipeline before typed Teams activity projection. The
projection does not retain the raw reply locator, so the Teams middleware saves
that locator in activity properties for the translator. The translator creates
a trusted approval action, the conversation actor routes it, and the session
binding compares it with the persisted pending approval before it calls the
shared approval flow.

The current binding check returns one generic rejection for several distinct
security gates. That result cannot identify whether the live Action.Execute
shape differs in session identity, destination, source-card locator, requester
identity, or other signed action fields. The existing HTTP test uses matching
synthetic sent and invoke activity IDs, so it does not establish the live SDK
source-card association.

## Goals / Non-Goals

**Goals:**

- Attribute every Teams approval validation rejection to one fixed safe code.
- Preserve the trusted SDK reply locator through translation and compare it to
  the persisted sent-card activity ID.
- Correct only a proven Teams boundary mismatch.
- Cover Personal, Posts, Threads, expiry, replay, and rejection behavior.
- Preserve the PR #46 serialized adaptive-card delivery path.

**Non-Goals:**

- Change the shared approval authority or another channel.
- Relax tenant, route, source-card, requester, nonce, offered-option, or
  replay validation.
- Deploy, merge, or run owner-controlled production checks.

## Decisions

### Classify each validation gate before changing its behavior

The Teams binding will evaluate each existing predicate separately and record
one fixed reason code before returning Rejected. It will not log identifiers,
tokens, raw activities, or card content. This makes the current live failure
observable without changing the security decision.

The alternative is a single generic rejection event. It cannot distinguish a
bad callback from a route mismatch and does not support a focused correction.

### Keep source-card binding and use the SDK invoke locator

The persisted prompt locator remains the activity ID returned when the approval
card is sent. The translator uses the saved raw invoke reply locator as the
source-card locator. A correction is permitted only where tests and SDK-shaped
evidence prove that Teams represents the same source card differently at this
boundary. It must preserve the client-provided prompt ID as an untrusted value
that is verified against persisted state.

The alternative is to drop the prompt comparison or accept a root activity ID.
That would allow a valid nonce from one card to act on another card and is not
acceptable.

### Keep routing isolated and do not manufacture an approval decision

Personal, Posts, and Threads routes must identify the expected binding before
the shared flow runs. An unknown or mismatched callback returns a neutral or
Rejected result as appropriate and does not create a decision. If actor
recovery must materialize a persisted binding, it may do so only for the exact
validated session identity and must then apply the full persisted binding
checks.

The alternative is to route a callback to a fresh unrelated binding. It hides
the failure as an empty pending state and weakens destination isolation.

### Preserve authoritative terminal cards

The shared approval result remains the source of terminal outcome. The Teams
adapter maps an accepted deny to Denied. Expiry remains a Teams terminal card
and reissues exactly one approval with a new nonce. Duplicates, replays, and
no-longer-pending requests remain neutral and do not call the shared flow.

## Risks / Trade-offs

- [A live SDK field differs from synthetic tests] → Add regression tests that
  preserve the raw invoke reply locator through middleware and test both card
  and root route shapes without accepting an unmatched locator.
- [Safe diagnostics could leak identifiers] → Use only the fixed reason-code
  set in telemetry and structured logs.
- [A recovery route can look like a fresh binding] → Require the exact
  destination and recover persisted state before accepting a correlation.
- [A terminal mapping regresses PR #46] → Retain and run the serializer and
  SDK reply-client regression tests.

## Migration Plan

1. Add the safe rejection attribution and its focused test matrix.
2. Add the narrow proven boundary correction and end-to-end invoke regression.
3. Run focused Teams and cross-channel shared-flow gates, then the full
   solution and quality gates.
4. Open a pull request against `dev` for owner review. Do not merge or deploy.

Rollback is a normal revert of this isolated Teams-only pull request. Persisted
approval events and the shared approval contract do not change.
