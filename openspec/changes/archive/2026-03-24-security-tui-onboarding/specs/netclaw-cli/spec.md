# netclaw-cli Delta Spec

## MODIFIED Requirements

### Requirement: Shared Slack user listing service

The user-fetching logic in `LookupSlackUserTool` (pagination via
`IUsersApi.List`, bot/deleted filtering, caching) SHALL be extracted into
a shared service that both the tool and the init wizard can consume. The
tool's existing behavior is unchanged — it delegates to the shared service
instead of owning the pagination logic directly.

#### Scenario: Init wizard uses shared user listing

- **GIVEN** the bot token is validated in ChatServices
- **WHEN** the ACL step loads
- **THEN** the shared user listing service fetches workspace users via
  `IUsersApi` using the same pagination and filtering as `LookupSlackUserTool`
- **AND** results are cached for the wizard session

#### Scenario: LookupSlackUserTool delegates to shared service

- **GIVEN** the daemon is running with a valid Slack connection
- **WHEN** the agent calls `lookup_slack_user`
- **THEN** the tool delegates to the shared user listing service
- **AND** existing behavior (query matching, result formatting) is unchanged

#### Scenario: API failure surfaces error to caller

- **GIVEN** the `users.list` call fails (missing scope, network error)
- **WHEN** the init wizard or tool attempts to list users
- **THEN** the failure reason is returned to the caller (not swallowed)
- **AND** no unhandled exception is thrown
- **AND** the caller decides how to present the error (wizard blocks,
  tool returns error message)
