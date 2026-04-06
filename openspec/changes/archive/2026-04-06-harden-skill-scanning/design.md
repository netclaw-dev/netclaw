## Context

The current `SkillScanner.Scan()` API returns only accepted `SkillEntry`
instances. Any malformed or unsafe skill is quietly omitted. That behavior is
now on the critical path for daemon startup (`Program.cs`), feed sync
rebuilds (`SystemSkillSyncService`), and operator/agent mutations
(`SkillManageTool`).

The scanner also trusts the raw filesystem shape more than it should. It walks
directories with `Directory.GetDirectories`, accepts whichever duplicate skill
name it sees first, and enumerates resources recursively without rejecting
symlinked trees. For a system that loads procedural instructions into the agent
context, that is too permissive.

Constraints:

- keep the MVP file-based skill model and existing `SKILL.md` layout
- preserve directory-based trust tier inference
- do not make startup depend on new services or persistence
- fail loudly on invalid skill state without taking down the whole daemon for a
  single bad operator file

## Goals / Non-Goals

**Goals:**

- make skill scanning canonical and bounded to the configured skills root
- reject ambiguous skill identity (duplicate names, frontmatter/name mismatch)
- return structured issues so callers can log and expose degraded state
- keep accepted skills available even when unrelated skills are rejected

**Non-Goals:**

- antivirus or prompt-injection classification of skill content
- signature verification for local operator skills
- redesigning the skill registry or compressed index format
- changing trust-tier semantics or feed publication workflow

## Decisions

### D1: Replace silent omission with structured scan results

`SkillScanner` should produce a result object containing both accepted entries
and rejected-item issues, rather than only the accepted list. Each issue should
carry at least the offending path, a machine-readable kind, and a human-readable
message.

Why this over logging inside the scanner:

- the scanner is shared by startup, sync, and tool code paths
- callers need to decide whether to log, return tool output, or mark degraded
  state
- keeping the scanner pure avoids binding actor/runtime code to logging

Alternative considered: keep `Scan()` returning entries and add side-channel
logging. Rejected because it would still hide failures from tool callers and
tests.

### D2: Scan only canonical in-root paths and reject symlink traversal

Before accepting a skill directory, `SKILL.md`, or resource subtree, the scanner
should resolve the canonical full path and verify it remains inside the expected
root. Any symlink in the traversed skill path should cause that skill to be
rejected with an explicit issue.

Why this over best-effort acceptance:

- skills shape agent behavior, so an out-of-root or symlinked path is a trust
  boundary problem, not cosmetic corruption
- rejecting the whole skill is simpler and safer than selectively pruning files

Alternative considered: allow symlinked skill directories but reject only
resource files that escape root. Rejected because the main `SKILL.md` itself is
the most sensitive file.

### D3: Duplicate names and frontmatter mismatches are invalid inventory states

If two scanned skills normalize to the same name, neither conflicting entry
should be registered until the conflict is resolved. If frontmatter `name` is
present and does not match the directory/tool target name, the skill should be
rejected.

Why this over first-wins behavior:

- first-wins is nondeterministic across directory order and hides the operator's
  mistake
- the agent must not load procedural instructions from an ambiguous identity

Alternative considered: prefer `.system` over user skills on collision.
Rejected because it still leaves a broken local inventory state hidden from the
operator.

### D4: Callers surface degraded state instead of crashing startup

Registry rebuild callers should register only accepted entries, then surface the
scan issues loudly through logs and tool output. Startup should continue with a
degraded skill inventory unless no valid skills remain for a required system
path.

Why this over fail-fast daemon startup:

- a single bad operator skill should not block chat, memory, or daemon health
- explicit degraded reporting satisfies the no-silent-fallback rule without
  turning routine repair work into an outage

Alternative considered: abort daemon startup on any scan issue. Rejected as too
disruptive for operator-authored local files.

## Risks / Trade-offs

- [Risk] Existing repos may already contain duplicate or mismatched skills that
  suddenly stop loading. -> Mitigation: emit explicit path-level diagnostics and
  keep unaffected skills registered.
- [Risk] Symlink rejection may surprise operators using convenience links. ->
  Mitigation: document the rule and require real directories for trusted skill
  content.
- [Risk] More detailed scan results increase caller complexity. -> Mitigation:
  centralize registry rebuild helpers so startup and tool flows share the same
  handling.
- [Risk] Partial inventory may change compressed skill menus at runtime. ->
  Mitigation: rebuild menus only from accepted entries and attach issue counts to
  the same rebuild event/log.

## Migration Plan

1. Introduce the structured scan result model and adapt current scanner tests.
2. Update registry rebuild paths in daemon startup, feed sync, and skill tools
   to consume accepted entries plus issues.
3. Add validation for frontmatter/name mismatch in `skill_manage` before write.
4. Add regression tests for duplicates, symlinked directories, unreadable files,
   and degraded rebuild reporting.

No persistence migration is required. Actor boundaries remain unchanged because
skill scanning stays outside session actors and only updates registry/context
state already held in DI singletons.

## Open Questions

- Should scan issues be persisted for later `netclaw doctor` display, or are
  startup/tool logs sufficient for the first pass?
- Should a duplicate involving `.system` skills produce a stronger severity than
  a duplicate involving only operator-authored skills?
