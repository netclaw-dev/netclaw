## ADDED Requirements

### Requirement: Deterministic evidence-backed completion after persistent post-tool empty responses
The session system SHALL treat repeated empty completion responses after successful tool iterations as a degraded finalization path instead of automatically classifying the turn as a generic provider failure. During the active turn, the session SHALL retain a bounded set of usable evidence from successful tool results. When bounded post-tool completion attempts continue returning no user-visible text, no file output, and no additional tool calls, the session SHALL synthesize and emit a deterministic best-effort text reply from that evidence as the terminal turn outcome.

#### Scenario: Successful tool work plus persistent empty completions yields synthesized reply
- **GIVEN** the active turn has completed one or more successful tool calls with usable evidence
- **AND** post-tool completion attempts keep returning no assistant text and no further tool calls
- **WHEN** the session reaches its bounded empty-response threshold
- **THEN** the session emits a deterministic best-effort text reply derived from the retained evidence
- **AND** the turn completes without being classified as a generic provider failure

#### Scenario: Persistent empty completions without usable evidence preserve provider failure
- **GIVEN** post-tool completion attempts keep returning no assistant text and no further tool calls
- **AND** the active turn has no usable successful tool evidence for fallback synthesis
- **WHEN** the session reaches its bounded empty-response threshold
- **THEN** the session emits the existing generic provider-failure terminal outcome
- **AND** it does not synthesize a reply from failed, empty, or unusable tool results

#### Scenario: Evidence-backed fallback remains transport-agnostic terminal text
- **GIVEN** the session synthesized a best-effort reply from retained tool evidence
- **WHEN** subscribers receive the terminal outputs for the turn
- **THEN** they receive the synthesized reply as ordinary session text output and normal turn completion signals
- **AND** the session does not require an adapter-specific fallback output type to make the reply user-visible
