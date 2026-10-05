# Mattermost local integration tests

The suite starts a fresh Docker server on a random local port.
It seeds test accounts and deletes the container after the tests.
It uses no live Mattermost credentials or server.

Run the suite:

```bash
NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS=1 dotnet test src/Netclaw.Channels.Mattermost.IntegrationTests/Netclaw.Channels.Mattermost.IntegrationTests.csproj
```

Run only the reminder cases:

```bash
NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS=1 dotnet test src/Netclaw.Channels.Mattermost.IntegrationTests/Netclaw.Channels.Mattermost.IntegrationTests.csproj --filter FullyQualifiedName~MattermostReminderIntegrationTests
```

The reminder cases cover issue [#1284](https://github.com/netclaw-dev/netclaw/issues/1284):

- `set_reminder` accepts Mattermost `current_session` delivery.
- The persisted definition retains the session ID, channel type, audience, and boundary.
- The execution reaches the real gateway, conversation actor, and session binding.
- A successful post reaches the original channel and thread as the bot account.
- An existing session uses one pipeline instance.
- A cold route creates the pipeline for the original session ID.
- A rejected HTTP post produces a failed outcome and no reply.
- A Team session cannot save a Personal reminder.

The cases use a deterministic model substitute through `ISessionPipeline`.
They use the real reminder manager, definition store, delivery observer, driver, and Mattermost HTTP API.
They inject a scheduler envelope after creation, so the tests need no wall-clock wait.
The manager removes a successful one-shot reminder and its history through its existing cleanup path.

The typing case covers issue [#2347](https://github.com/netclaw-dev/netclaw/issues/2347):

- A session binding that reports an active session sends native typing pulses.
- A separate user receives the `typing` events on a raw WebSocket.
- Each event has the bot user ID, the channel ID, and the thread root post ID.
- The second event proves the repeat pulse from the binding timer.

Run only the typing case:

```bash
NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS=1 dotnet test src/Netclaw.Channels.Mattermost.IntegrationTests/Netclaw.Channels.Mattermost.IntegrationTests.csproj --filter FullyQualifiedName~MattermostTypingIntegrationTests
```

The fixture uses `mattermost/mattermost-preview:latest`.
The local proof on 2026-10-05 used Mattermost Server 11.11.1.
Its image digest was `sha256:90319a856c49f0aef1000ab473ad76be3a8a97bef196424764638ee132eb61bc`.
