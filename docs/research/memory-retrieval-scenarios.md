# Memory Retrieval Scenarios

This document captures concrete retrieval scenarios for evolving Netclaw from
LLM-planned recall toward a more deterministic, metadata-driven architecture.

The goal is to make retrieval expectations explicit before committing to a
production implementation.

## How To Read This

Each scenario describes:

- where the message happened
- what memory already exists
- what the new query is
- what hard scope should apply
- what soft scope should activate
- whether the result should be a ranked hit, a bundle, or empty

These scenarios are intended to become:

- extractor tests
- retrieval tests
- eval fixtures
- design validation notes

## Scope Model

- **Hard scope**: system-owned boundary from runtime metadata such as Slack
  workspace, channel, DM participant, and thread.
- **Soft scope**: topic/title/facet/anchor activation inferred from the active
  thread and the current prompt.
- **Mode**:
  - `ranked`: one or a few top memories should win
  - `bundle`: multiple slots should be filled for a composite answer
  - `empty`: nothing should auto-recall

## Scenario Bank

### 1. DM Travel Preference Recall

- **Context**: Slack DM with Aaron
- **Prior memory**:
  - `origin airport = IAH`
  - `preferred airline = United Airlines`
- **Query**: `What airline do I usually take?`
- **Hard scope**: `user:aaron`
- **Soft scope**: `travel_profile`
- **Mode**: `ranked`
- **Expected retrieval**:
  - top hit: `preferred_airline = United Airlines`
  - no unrelated project memories

### 2. DM Travel Preference Bundle

- **Context**: Slack DM with Aaron
- **Prior memory**:
  - `origin airport = IAH`
  - `preferred airline = United Airlines`
- **Query**: `When I book flights, what airport and airline do I usually use?`
- **Hard scope**: `user:aaron`
- **Soft scope**: `travel_profile`
- **Mode**: `bundle`
- **Expected retrieval**:
  - `origin_airport -> IAH`
  - `preferred_airline -> United Airlines`

### 3. DM Broad Travel Prompt Should Still Recall Preferences

- **Context**: Slack DM with Aaron
- **Prior memory**:
  - `origin airport = IAH`
  - `preferred airline = United Airlines`
- **Query**: `If I wanted to fly to Boston in October how much would I have to pay round trip?`
- **Hard scope**: `user:aaron`
- **Soft scope**: `travel_profile`
- **Mode**: `bundle`
- **Expected retrieval**:
  - `origin_airport -> IAH`
  - `preferred_airline -> United Airlines`
- **Why this matters**:
  - the prompt does not explicitly say `airport` or `airline`
  - a good system should still activate travel-profile memories

### 4. DM Composite Trip Planning

- **Context**: Slack DM with Aaron
- **Prior memory**:
  - `origin airport = IAH`
  - `preferred airline = United Airlines`
  - `Stir Trek 2026 trip recommendation`
- **Query**: `I'm speaking at Stir Trek 2026 - I fly out of IAH. What's the best flight / hotel combination for me? Closest to the venue preferably. And do you think I'll need a rental car?`
- **Hard scope**: `user:aaron`
- **Soft scope**: `travel_profile + trip_planning`
- **Mode**: `bundle`
- **Expected retrieval**:
  - `origin_airport -> IAH`
  - `preferred_airline -> United Airlines`
  - `trip_plan -> Stir Trek 2026 travel recommendation`

### 5. DM Follow-Up Preference Failure Regression

- **Context**: Slack DM with Aaron
- **Prior memory**:
  - `origin airport = IAH`
  - `preferred airline = United Airlines`
- **Query**: `So you don't remember my travel preferences?`
- **Hard scope**: `user:aaron`
- **Soft scope**: `travel_profile`
- **Mode**: `bundle`
- **Expected retrieval**:
  - `origin_airport -> IAH`
  - `preferred_airline -> United Airlines`
- **Why this matters**:
  - this is an intentionally indirect prompt
  - it should still recall the stored preference bundle

### 6. DM Project Narrowing Inside Broad Personal Scope

- **Context**: Slack DM with Aaron
- **Prior memory**:
  - TextForge pricing decisions
  - Netclaw implementation notes
  - travel preferences
  - family preferences
- **Query**: `What's the pricing model for TextForge?`
- **Hard scope**: `user:aaron`
- **Soft scope**: `project:textforge`
- **Mode**: `ranked`
- **Expected retrieval**:
  - TextForge pricing memory wins
  - travel/family/Netclaw memories do not appear

### 7. Ops Channel Incident Recovery

- **Context**: Slack alert/ops channel for one application
- **Prior memory**:
  - recovery procedure for queue lag
  - dashboard reference for queue health
- **Query**: `The queue is piling up again. What did we do last time to get backlog under control?`
- **Hard scope**: channel/project domain
- **Soft scope**: `incident_recovery`
- **Mode**: `ranked`
- **Expected retrieval**:
  - recovery action is top hit
  - dashboard may appear as support, not as the winner

### 8. Ops Channel Reference Lookup

- **Context**: same alert/ops channel
- **Prior memory**:
  - recovery procedure
  - dashboard reference
- **Query**: `Where's the dashboard for this?`
- **Hard scope**: channel/project domain
- **Soft scope**: same service/topic, reference intent
- **Mode**: `ranked`
- **Expected retrieval**:
  - dashboard/reference memory is top hit

### 9. Channel-Learned Operational Bias

- **Context**: long-lived alert channel for one service
- **Prior channel profile**:
  - repeated incident, queue, dashboard, runbook, and service terms
- **Query**: `What's the usual fix here?`
- **Hard scope**: channel/project domain
- **Soft scope**: operational profile + active service hints
- **Mode**: `ranked`
- **Expected retrieval**:
  - operational recovery memories are favored
  - unrelated product/marketing memories are excluded

### 10. Public Channel Coarse Project Boundary

- **Context**: `#textforge`
- **Prior memory**:
  - TextForge project decisions
  - unrelated personal travel preferences also exist elsewhere
- **Query**: `What did we decide about pricing?`
- **Hard scope**: `channel:#textforge`
- **Soft scope**: pricing/product topic
- **Mode**: `ranked`
- **Expected retrieval**:
  - TextForge pricing memory
  - no personal travel preference memories

### 11. Privacy Suppression

- **Context**: any DM or channel
- **Prior memory**:
  - secret token/credential stored with secret sensitivity
- **Query**: `Do you have any private credentials or tokens from old setup notes?`
- **Hard scope**: context-dependent
- **Soft scope**: irrelevant
- **Mode**: `empty`
- **Expected retrieval**:
  - no auto-recalled secret memory

### 12. Searchable Evidence But Not Auto Recall

- **Context**: Slack DM with Aaron
- **Prior memory**:
  - hotel recommendation and rental-car advice for Stir Trek stored as time-bounded evidence
- **Query**: `What airline do I use?`
- **Hard scope**: `user:aaron`
- **Soft scope**: `travel_profile`
- **Mode**: `ranked`
- **Expected retrieval**:
  - airline preference only
  - hotel evidence excluded from auto recall

### 13. Searchable Evidence On Explicit Retrieval

- **Context**: Slack DM with Aaron
- **Prior memory**:
  - same Stir Trek hotel/recommendation evidence
- **Query**: `What hotel options did we talk about for Stir Trek?`
- **Hard scope**: `user:aaron`
- **Soft scope**: `trip_planning`
- **Mode**: `bundle` or explicit search result set
- **Expected retrieval**:
  - event-specific hotel evidence appears
  - time-bounded trip-planning memory is allowed because this is explicit retrieval intent

### 14. Topic Drift In A Long DM

- **Context**: Slack DM with Aaron
- **Earlier turns**:
  - travel planning
- **Later query**: `What should we call this feature on the homepage?`
- **Hard scope**: `user:aaron`
- **Soft scope**: shifts from `travel_profile/trip_planning` to `marketing/product messaging`
- **Mode**: `ranked`
- **Expected retrieval**:
  - no travel memories unless they are somehow directly relevant
  - if no marketing memory exists, better to return empty than to pollute with travel results

## Design Implications

These scenarios imply:

- hard scope must come from runtime metadata, not from the LLM
- DMs need broad hard scope but narrow soft scope
- thread titles and topic summaries are soft-scope hints, not security boundaries
- some prompts are best answered by ranked retrieval
- some prompts are best answered by bundle/slot retrieval
- write-time metadata extraction is critical for deterministic retrieval quality

## Next Uses

This scenario bank can be turned into:

- PoC fixture expansion
- extractor-output contract tests
- retrieval integration tests
- eval fixture definitions
- design review checklists for future memory changes
