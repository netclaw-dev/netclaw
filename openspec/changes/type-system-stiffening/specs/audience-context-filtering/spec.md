## ADDED Requirements

### Requirement: Audience derivation has no default-audience fallback

The session pipeline SHALL derive a turn's audience only from the explicitly
supplied turn source. There SHALL be no pipeline-level `DefaultAudience`,
`DefaultBoundary`, `DefaultPrincipal`, or `DefaultProvenance` configuration
property. A turn that reaches audience derivation without a turn source SHALL
fail loudly rather than adopt a default audience.

#### Scenario: No default-audience configuration exists

- **WHEN** session pipeline options are constructed
- **THEN** there is no `DefaultAudience` (or sibling `Default*` trust) property
  to set
- **AND** trust context can only enter the pipeline by way of an inbound
  `ChannelInput`

#### Scenario: Audience derivation uses the supplied turn source

- **GIVEN** a turn with an explicit turn source carrying `TrustAudience.Personal`
- **WHEN** the pipeline derives the effective audience
- **THEN** the derived audience reflects the Personal source audience
- **AND** no default-audience value participates in the derivation
