## 1. Tool metadata and routing primitives

- [x] 1.1 Extend tool registration metadata so tools can declare post-tool required skill names.
- [x] 1.2 Register `search-citation` as a post-tool required skill for `web_search` and `web_fetch`.
- [x] 1.3 Add unit coverage for tool metadata/routing so duplicate required skills collapse into one deterministic set.

## 2. Session follow-up call integration

- [x] 2.1 Update `LlmSessionActor` to collect required skills from completed tools and resolve them before the follow-up `FireLlmCall()`.
- [x] 2.2 Reuse the existing session auto-load cache/injection path for tool-triggered skills and log the auto-load reason as `post-tool`.
- [x] 2.3 Degrade safely when a mapped skill is missing or unreadable, with warning logs and no turn failure.

## 3. Verification and skill sync

- [x] 3.1 Add integration tests covering `web_search`/`web_fetch` turns, cached reinjection on repeated follow-up calls, and post-tool empty-response nudges retaining the loaded skill.
- [x] 3.2 Update `feeds/skills/.system/files/search-citation/SKILL.md` to document the enforced post-search citation behavior and bump `metadata.version`.
- [x] 3.3 Run `dotnet build`, targeted actor/tool tests, and `dotnet slopwatch analyze` to verify the change compiles cleanly and introduces no new violations.
