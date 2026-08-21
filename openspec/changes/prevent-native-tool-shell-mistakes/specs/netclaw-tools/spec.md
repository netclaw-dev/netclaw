## ADDED Requirements

### Requirement: Attachment tool accepts an authorized source path directly

The parent-session model-visible `attach_file` definition SHALL tell the agent to pass the existing authorized source path directly. The agent SHALL NOT need to copy the file into session scratch before calling the tool. Netclaw SHALL retain the existing audience, read-deny, proximity, and safe-copy behavior inside the tool. Sub-agents SHALL NOT receive this tool until an internal typed attachment handoff can deliver child attachments to the parent invocation.

#### Scenario: Interactive Personal agent attaches an existing project file directly

- **GIVEN** an interactive Personal parent session can attach an existing project file under current policy
- **WHEN** the model needs to send that file to the user
- **THEN** the initial tool set contains `attach_file`
- **AND** its definition accepts the source path directly
- **AND** Netclaw performs any required copy into the session attachments directory
- **AND** no shell copy is required

#### Scenario: Core exposure does not widen attachment reach

- **GIVEN** current audience or path policy denies an attachment source
- **WHEN** `attach_file` is present in the registered core
- **THEN** the model-visible set still filters the tool by audience policy
- **AND** the tool still rejects the denied source when invoked
