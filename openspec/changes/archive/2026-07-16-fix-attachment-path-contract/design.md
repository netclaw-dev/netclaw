## Context

Accepted channel attachments are moved into a collision-safe `inbox/` path and announced with that session-relative path. Inline media is also persisted under `media/` with an opaque GUID so chat history can rehydrate normalized model input. Both directories are children of the session directory, but exposing `media_dir` beside an `inbox/...` announcement gives the model two plausible roots and has caused it to inspect the wrong one.

Slack, Discord, and Mattermost share attachment formatting and media persistence, so the correction belongs at those shared seams. Actor messages and persisted media references remain unchanged.

## Goals / Non-Goals

**Goals:**

- Make the final collision-safe inbox path the single authoritative agent-facing attachment location.
- Make the path base unambiguous without exposing additional host filesystem information.
- Preserve identical behavior across live ingress and thread-history hydration for all supported attachment channels.

**Non-Goals:**

- Changing inbox collision or historical naming algorithms.
- Changing GUID media naming, normalization, persistence, or model rehydration.
- Adding deduplication, retention, cleanup, or configuration.

## Decisions

1. **Keep attachment paths session-relative.** Announcements continue to persist `path="inbox/..."`, derived from the final path returned by the inbox writer. This remains valid if the Netclaw home moves. Persisting absolute paths was rejected because it would become stale after relocation and would require audience-dependent announcement formats to preserve Public path redaction.
2. **Expose only `session_dir` as the trusted filesystem root.** Team and Personal static context no longer advertises `media_dir`. The attachment guidance states that `path` is relative to `session_dir`, already contains collision-safe renaming, and must not be resolved through internal media storage. Public context continues to omit host paths.
3. **Keep the actor and persistence boundary unchanged.** Channel inputs still carry the announcement plus optional `DataContent`; `ChannelPipeline` still writes model media references under `media/`. No message, serializer, snapshot, or journal migration is required.
4. **Test the producer/consumer contract at shared seams.** Formatting tests prove the announcement matches the existing inbox file, channel contract coverage proves shared adoption, and session assembly tests prove only the intended root is exposed.

## Risks / Trade-offs

- **[Older persisted attachment announcements predate the stronger guidance]** → Their `inbox/...` paths already use the same relative contract, so the new static guidance interprets them correctly without rewriting history.
- **[Agents may still list session contents unnecessarily]** → Remove the competing `media_dir` hint and explicitly identify the announced path as authoritative.
- **[Internal media remains duplicate storage]** → Preserve it because it owns normalized model-history bytes; storage lifecycle changes are intentionally excluded.

## Migration Plan

Deploy as a prompt/specification correction with no data migration. Rollback restores the previous session block and guidance; persisted attachment and media records remain compatible in both directions.

## Open Questions

None.
