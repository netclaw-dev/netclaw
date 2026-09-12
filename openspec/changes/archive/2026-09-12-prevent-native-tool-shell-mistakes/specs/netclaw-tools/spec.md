## ADDED Requirements

The terms in this requirement use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: Attachment tool accepts an authorized source path directly

The parent-session model-visible `attach_file` definition SHALL tell the agent
to pass the existing authorized source path directly. The agent SHALL NOT need
to copy the file into managed temporary storage first.

Netclaw SHALL retain the existing audience, read-deny, proximity, and safe-copy
behavior. Subagents SHALL NOT receive this tool until an internal attachment
handoff can deliver child attachments to the parent invocation.

Example:

```text
interactive Personal parent model calls:
  attach_file(Path = "/workspace/project/report.pdf")

Netclaw:
  authorizes the source path
  copies it to the session attachment directory when required
  returns the attachment through the parent invocation

subagent:
  does not receive, find, load, or dispatch attach_file
  can report a saved path to the parent instead
```

Authority examples and counterexamples:

| Caller and source | Required result |
|---|---|
| Interactive Personal parent with an authorized project file | Attach it directly. Copy it into the session when required. |
| Parent with a protected credential path | Deny it. Core exposure does not bypass the read deny. |
| Team or non-interactive parent with a source outside the session tree | Deny it through the existing proximity rule. |
| Subagent with any source | Do not expose, find, load, or dispatch `attach_file`. |

#### Scenario: Interactive Personal agent attaches an existing project file directly

- **GIVEN** an interactive Personal parent session can attach an existing project file under current policy
- **WHEN** the model needs to send that file to the user
- **THEN** the initial tool set contains `attach_file`
- **AND** its definition accepts the source path directly
- **AND** Netclaw performs any required copy into the session attachments directory
- **AND** no shell copy is required

#### Scenario: Core exposure does not widen attachment reach

- **GIVEN** the path access decision denies an attachment source
- **WHEN** `attach_file` is present in the registered core
- **THEN** the model-visible set still filters the tool by audience policy
- **AND** the tool still rejects the denied source when invoked
