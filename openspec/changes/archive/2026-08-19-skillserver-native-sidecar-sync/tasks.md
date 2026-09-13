## 1. Dependency and API readiness

- [x] 1.1 Confirm SkillServer prerelease `0.4.0-beta.1` is published with native manifest and sub-agent artifact APIs.
- [x] 1.2 Update NetClaw package references to consume `Netclaw.SkillClient` `0.4.0-beta.1` or the final approved version for implementation.
- [x] 1.3 Confirm SkillClient API names for native manifest fetch, sub-agent traversal, `agent-md` selection, and verified artifact download.
- [x] 1.4 Keep RFC skill sync behavior unchanged before adding native sidecar logic.

## 2. Managed path and state model

- [x] 2.1 Add `NetclawPaths` helpers for `~/.netclaw/agents/.server-feeds/`, per-feed managed agent directories, and per-feed managed agent sync state.
- [x] 2.2 Add or reuse sync-state models for managed sub-agent name, version, SHA-256, and sync timestamp tracking.
- [x] 2.3 Add safe segment validation for feed names and sub-agent filenames before writing managed agent paths.
- [x] 2.4 Add atomic single-file replace helper for managed sub-agent writes.

## 3. Native sidecar sync implementation

- [x] 3.1 Refactor `ServerFeedSkillSyncService.SyncFeedAsync` so a successful RFC fetch with an empty index can still proceed to optional native sidecar sync.
- [x] 3.2 Add optional `/manifest.json` feature detection using `SkillServerClient` native manifest APIs after successful RFC index fetch.
- [x] 3.3 Treat missing, unavailable, malformed, or unsupported native sidecars as fail-soft outcomes that preserve existing managed sub-agents.
- [x] 3.4 Traverse native sub-agent resources and select the `agent-md` artifact for each advertised sub-agent version.
- [x] 3.5 Download each `agent-md` artifact with feed timeout and API key behavior consistent with RFC skill downloads.
- [x] 3.6 Verify SHA-256 digest before parsing or writing the downloaded artifact.
- [x] 3.7 Parse downloaded sub-agent frontmatter and require `name` to match the advertised manifest sub-agent name.
- [x] 3.8 Write verified sub-agents only under the managed per-feed namespace and update per-feed sub-agent sync state.
- [x] 3.9 Skip managed sub-agent pruning for any partial native sidecar sync failure.
- [x] 3.10 Prune only stale managed sub-agent files and state entries after a fully successful native sidecar sync for that feed.

## 4. Sub-agent loader and conflict behavior

- [x] 4.1 Extend `FileSubAgentDefinitionLoader` to include managed server-feed files in its fingerprint and load snapshot.
- [x] 4.2 Load user-authored top-level `~/.netclaw/agents/*.md` files before managed server-feed files.
- [x] 4.3 Preserve local user-authored precedence when a managed feed sub-agent has the same logical name.
- [x] 4.4 Implement deterministic managed-feed duplicate handling using configured feed order or another documented stable order.
- [x] 4.5 Emit diagnostics for shadowed managed sub-agents without exposing shadowed definitions through discovery, `spawn_agent`, or routed skill execution.

## 5. Tests

- [x] 5.1 Add daemon sync tests for feed without native sidecar preserving RFC skill sync behavior.
- [x] 5.2 Add daemon sync tests for successful native sub-agent download, digest verification, identity validation, managed write, and state update.
- [x] 5.3 Add daemon sync tests for native sidecar fetch failure, malformed manifest, digest mismatch, and artifact name mismatch preserving previous managed files.
- [x] 5.4 Add daemon sync tests proving partial native sidecar sync skips pruning.
- [x] 5.5 Add daemon sync tests proving successful native sidecar sync prunes only stale files in the same managed feed namespace.
- [x] 5.6 Add configuration loader tests proving managed server-feed agents load when no local conflict exists.
- [x] 5.7 Add configuration loader tests proving local user-authored agents shadow managed feed agents and routed lookups resolve to the local definition.
- [x] 5.8 Add configuration loader tests proving managed feed duplicate handling is deterministic and diagnostic.

## 6. Documentation and verification

- [x] 6.1 Update operator/developer documentation for SkillServer feeds and managed sub-agent sync if an appropriate docs page exists.
- [x] 6.2 Run targeted NetClaw daemon/configuration tests for skill sync and sub-agent loading.
- [x] 6.3 Run `openspec validate "skillserver-native-sidecar-sync"` and fix any artifact or spec issues.
- [x] 6.4 Run `dotnet build -c Release` after implementation.
- [x] 6.5 Run `dotnet test -c Release` or the agreed targeted subset plus any required full-suite follow-up.
- [x] 6.6 Run `dotnet slopwatch analyze` and resolve new violations.

## 7. Docker-backed integration spike

- [x] 7.1 Add or run a Testcontainers-based spike that starts a real `ghcr.io/netclaw-dev/skillserver:0.4.0-beta.1` container.
- [x] 7.2 Seed the real SkillServer instance through its HTTP API or CLI with a real skill and real sub-agent artifact.
- [x] 7.3 Configure NetClaw server-feed sync against the container endpoint and verify the RFC skill path plus native sub-agent sidecar path end-to-end.
- [x] 7.4 Verify managed sub-agent files land under `~/.netclaw/agents/.server-feeds/<feed-name>/` and local user-authored sub-agents still win conflicts.
- [x] 7.5 Confirmed the spike does not exercise actual sub-agent execution, so no inference call to `https://spark2.testlab.petabridge.net/` was required.
- [x] 7.6 Keep the Docker-backed spike self-skipping or opt-in on hosts without Docker or required inference credentials.
