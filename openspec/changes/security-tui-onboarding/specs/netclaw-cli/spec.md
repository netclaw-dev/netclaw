# netclaw-cli Delta Spec

## ADDED Requirements

### Requirement: Slack user list resolution

`ISlackProbe` SHALL provide a `ListUsersAsync` method that fetches workspace
users from the Slack `users.list` API with pagination support.

#### Scenario: Fetch workspace users

- **GIVEN** a valid bot token with `users:read` scope
- **WHEN** `ListUsersAsync` is called
- **THEN** a list of `SlackUser(DisplayName, RealName, Id)` records is returned
- **AND** bot users and deactivated users are excluded

#### Scenario: Missing users:read scope

- **GIVEN** a bot token without `users:read` scope
- **WHEN** `ListUsersAsync` is called
- **THEN** an empty list is returned
- **AND** no exception is thrown
