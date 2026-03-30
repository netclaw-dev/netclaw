## Context

Netclaw currently has a structural skill scanner and an `ISkillContentScanner`
interface, but the bound implementation is `NoOpSkillContentScanner`. That means
the product advertises content-scanning hooks without actually enforcing any
policy on skill files that the agent can later load into its prompt or read as
supporting resources.

The highest-risk write paths are already centralized:

- `skill_manage` mutates user-authored skills under `~/.netclaw/skills/`
- `SystemSkillSyncService` downloads signed system skills and writes them under
  `.system/`

Those are the smallest safe choke points for MVP enforcement because they do not
require a redesign of runtime registry rebuilds or the startup scan pipeline.

Constraints:

- keep the existing directory-based skill model and `skill_manage` contract
- preserve legitimate text-based resources in `references/`, `scripts/`, and
  `assets/`
- fail closed when Netclaw itself cannot determine that new skill content is
  safe enough to persist
- avoid introducing model-based or network-bound scanners on the write path
- keep actor boundaries unchanged; enforcement lives in DI services and daemon
  sync/tool layers, not session actors

## Goals / Non-Goals

**Goals:**

- replace the no-op scanner with an enforced production implementation
- apply the scanner to every Netclaw-controlled skill write path
- reject binary or unsupported resource payloads before they reach disk
- run prompt-injection detection on prompt-bearing skill text with bounded,
  explainable failure behavior
- preserve the previously accepted skill version when a sync update is rejected

**Non-Goals:**

- rescanning every pre-existing skill file during generic startup discovery
- content classification for arbitrary downloaded binaries or future marketplace
  packages
- LLM-based moderation, cloud scanning services, or signature changes
- changing trust-tier inference or the slash-command registry model
- adding new CLI `doctor` surfaces in this change

## Decisions

### D1: Enforce at Netclaw-controlled write boundaries first

The new scanner will run in `skill_manage` and `SystemSkillSyncService`, the two
places where Netclaw itself persists skill content. This gives immediate
coverage for agent self-modification and signed feed sync without converting the
entire structural scan pipeline into an async content-validation system.

Why this over startup-wide rescanning first:

- it closes the most important self-write and feed-ingestion paths now
- it keeps the implementation small enough for MVP-safe adoption
- it avoids pushing async detector semantics through `SkillScanner` and every
  startup caller in the same change

Alternative considered: retrofit `SkillScanner.Scan()` into a content-aware async
pipeline used everywhere. Rejected for this change because it is broader than
necessary and would entangle structural discovery with write-time enforcement.

### D2: Use a deterministic text-policy scanner, not the image upload scanner

Skill content needs different validation than Slack file uploads. The existing
`MagicByteContentScanner` only accepts images and always blocks shebang content,
which would incorrectly reject legitimate text resources and script helpers.

The new skill scanner should instead classify files by skill role:

- `SKILL.md` and `references/*`: UTF-8 text, size-limited, no NUL bytes,
  prompt-injection checked
- `assets/*`: UTF-8 text templates/config snippets only for MVP
- `scripts/*`: UTF-8 text only, with a small allowlist of text script
  extensions; binary executables remain rejected

Why this over reusing `MagicByteContentScanner`:

- skill resources are text-first, not image-first
- shell/python helper scripts are expected in skill directories
- the scanner needs path-aware policy, not MIME-only policy

Alternative considered: allow arbitrary binary assets in `assets/`. Rejected
because current skill tooling is text-oriented (`skill_read_resource` returns
text) and binary assets would expand the trust surface without a real use case.

### D3: Prompt-bearing files get injection detection; detector failures reject

`SKILL.md`, `references/*`, and text assets that may be loaded into context
should pass through `IPromptInjectionDetector`. A `High` risk result rejects the
write. Detector exceptions or timeouts also reject the pending write/update with
an explicit reason; Netclaw must not silently downgrade back to "allow all."

Why this over fail-open behavior:

- the repo's no-silent-fallback rule applies directly here
- skill files are durable procedural instructions, not ephemeral chat input
- operators need a visible failure they can repair, not an invisible bypass

Alternative considered: keep `NullPromptInjectionDetector` in production and only
enforce the scanner when a future detector exists. Rejected because that would
preserve the current gap the change is meant to close.

### D4: Sync updates are staged and swap only after all files pass

System skill sync should download files into a staging directory, scan every
downloaded file, and only replace the installed skill directory when the full
candidate version passes. If any file fails, the sync logs the rejection and
retains the previously accepted version on disk.

Why this over writing files incrementally in place:

- avoids partial upgrades that mix trusted old files with rejected new files
- keeps sync failure recovery trivial: do nothing and retain the last good copy
- aligns with the existing signed-manifest trust model without broadening it

Alternative considered: scan only `SKILL.md` and trust resource files from the
signed feed. Rejected because resources can still be loaded into prompts or read
by the agent, so they need the same policy gate.

## Risks / Trade-offs

- [Risk] Existing system skill resources may rely on file types the new policy
  rejects. -> Mitigation: keep the MVP allowlist explicit and update built-in
  skill resources/documentation in the same PR.
- [Risk] Prompt-injection heuristics may false-positive on code-like content. ->
  Mitigation: limit prompt detection to prompt-facing text and treat `scripts/*`
  as text-policy-only unless explicitly read into prompt context later.
- [Risk] Fail-closed detector errors could block legitimate edits during a bug in
  the detector. -> Mitigation: keep the detector local/bounded, return clear
  reasons, and preserve the prior on-disk version.
- [Risk] Manual file edits outside Netclaw remain unscanned until touched by a
  managed write path. -> Mitigation: document this as a known gap and consider a
  future startup or `doctor` pass if needed.

## Migration Plan

1. Introduce the production skill content scanner and replace the DI default.
2. Wire the scanner into `skill_manage` for `create`, `edit`, `patch`, and
   `write_file`, including resource-path-aware scanning.
3. Update system skill sync to stage downloads, scan `SKILL.md` plus resource
   files, and atomically swap only when the candidate set passes.
4. Update the `skill-authoring` system skill to document the enforced file
   policy and rejection modes.
5. Add targeted tests, then run `dotnet slopwatch analyze` and
   `./evals/run-evals.sh` if the system skill text changes in implementation.

Rollback is straightforward: restore the no-op registration and previous sync
write path, because no persisted schema or actor protocol changes are involved.

## Open Questions

- Should text assets be limited to a filename-extension allowlist, or is UTF-8 +
  size/NUL-byte validation sufficient for MVP?
- Do we want `Medium` prompt-injection findings to warn-but-allow, or should the
  first implementation reject only `High` to minimize false positives?
- Should a future follow-up make startup discovery rescan on-disk content so
  manually edited skill files are also enforced?
