# netclaw-slack-socket Delta Spec

## ADDED Requirements

### Requirement: Thread history fetch via conversations.replies

The Slack adapter SHALL fetch thread history using Slack's `conversations.replies`
API when a new session is created from an `app_mention` in an existing thread.
The fetch SHALL paginate through all replies, download image attachments using
the bot token, and content-scan each image through the existing scanner pipeline.
The bot's own messages and other bot messages SHALL be excluded from the result.

#### Scenario: Full thread history fetched on first mention

- **GIVEN** a thread in channel `C0123` with thread_ts `T456` has 15 prior
  messages
- **WHEN** an `app_mention` event creates a new session for `C0123/T456`
- **THEN** the adapter calls `conversations.replies` with channel `C0123` and
  ts `T456`
- **AND** returns all 15 prior messages as `ChannelInput` items (excluding
  any bot messages)

#### Scenario: Paginated fetch for long threads

- **GIVEN** a thread has more than 1000 messages (Slack's per-page limit)
- **WHEN** the adapter fetches thread history
- **THEN** the adapter paginates using the cursor returned by each response
- **AND** all pages are fetched until no cursor remains

#### Scenario: Image attachments downloaded and scanned

- **GIVEN** a historical thread message has an image attachment
- **WHEN** the history fetcher processes that message
- **THEN** the image is downloaded via `url_private_download` with Bearer
  token auth
- **AND** the image is content-scanned via `IContentScanner`
- **AND** the image bytes are included as `DataContent` in the `ChannelInput`

#### Scenario: Bot messages filtered from history

- **GIVEN** a thread contains messages from users, the Netclaw bot, and a CI bot
- **WHEN** the history fetcher processes the thread
- **THEN** messages with a `bot_id` matching the Netclaw bot are excluded
- **AND** messages with any other `bot_id` are excluded
- **AND** only human user messages are included in the result

#### Scenario: Rate limit handled with retry

- **GIVEN** the Slack API returns HTTP 429 (rate limited)
- **WHEN** the adapter is fetching thread history
- **THEN** the adapter retries after the `Retry-After` interval
- **AND** the backfill completes once the rate limit clears

#### Scenario: API error does not block session creation

- **GIVEN** `conversations.replies` returns a permission error or server error
- **WHEN** the adapter is fetching thread history
- **THEN** the adapter logs a warning with the error details
- **AND** the session proceeds without backfilled context
- **AND** the triggering mention message is still delivered to the session
