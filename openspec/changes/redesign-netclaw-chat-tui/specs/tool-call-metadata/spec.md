## MODIFIED Requirements

### Requirement: Rationale is required

The `_rationale` field SHALL be a required string in every tool schema. Its
description SHALL instruct the model to state its intent in one sentence.

The shared execution preflight SHALL reject a new tool call when `_rationale`
is absent, blank, or not a string. The tool SHALL NOT execute. The rejection
SHALL identify `_rationale` and ask the model to issue a corrected call. The
rejection SHALL occur before an approval request.

The persistence extractor and transcript reader SHALL accept old records that
have no rationale. A client SHALL mark the old rationale as unavailable. It
SHALL NOT infer intent from arguments or other tool fields.

#### Scenario: Model provides rationale on a tool call

- **GIVEN** the model issues a new tool call
- **WHEN** the call includes a nonempty string `_rationale`
- **THEN** the preflight accepts the rationale
- **AND** the pipeline stores it on `ToolCallMeta`
- **AND** normal authorization and dispatch continue

#### Scenario: New tool call omits rationale

- **GIVEN** the model issues a new tool call without `_rationale`
- **WHEN** the shared execution preflight validates the call
- **THEN** it produces a correction result for that call
- **AND** the tool does not execute
- **AND** no approval request occurs

#### Scenario: New tool call supplies an invalid rationale

- **GIVEN** the model supplies a blank or non-string `_rationale`
- **WHEN** the shared execution preflight validates the call
- **THEN** it produces a correction result that names `_rationale`
- **AND** the tool does not execute

#### Scenario: One parallel call omits rationale

- **GIVEN** a parallel batch contains one compliant call and one call without
  `_rationale`
- **WHEN** the pipeline executes the batch
- **THEN** the compliant call can execute
- **AND** the noncompliant call returns a correction result
- **AND** both calls retain their original call identities

#### Scenario: Old transcript has no rationale

- **GIVEN** an old settled tool record has no rationale
- **WHEN** a current client reads the transcript
- **THEN** the record remains readable
- **AND** the client marks its rationale as unavailable
- **AND** the client does not invent a rationale
