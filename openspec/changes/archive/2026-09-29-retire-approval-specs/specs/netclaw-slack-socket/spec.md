## ADDED Requirements

### Requirement: Resolution message single-line format

After Netclaw accepts an approval answer, the Slack prompt SHALL show one
resolution line in place of the buttons. For a shell prompt the line SHALL name
the verbs and the scope:

| Answer | Resolution line |
|---|---|
| Always here | `Saved: <verbs> in <location>` |
| This repository | `Saved: <verbs> in this repository` |
| Always anywhere | `Saved: <verbs> anywhere` |
| This chat | `Saved for this chat: <verbs> in <location>` |
| Once | `Approved (no save)` |
| Deny | `Denied` |

For an MCP tool the line SHALL be `Always allowed: <tool>`, `Allowed for this
chat: <tool>`, `Allowed once`, or `Denied`. An unknown key SHALL show
`Resolved`.

#### Scenario: Folder grant resolution line

- **GIVEN** a shell prompt for `git status` in `~/repos/foo/`
- **WHEN** the requester selects `Always here`
- **THEN** the prompt shows `Saved: git status in ~/repos/foo/`

#### Scenario: One-time answer saves nothing

- **GIVEN** a shell prompt for `npm test`
- **WHEN** the requester selects `Once`
- **THEN** the prompt shows `Approved (no save)`
