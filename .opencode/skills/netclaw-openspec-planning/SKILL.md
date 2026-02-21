---
name: netclaw-openspec-planning
description: Netclaw-specific OpenSpec planning workflow. Use when creating or updating PRD-aligned OpenSpec changes for Netclaw.
license: MIT
compatibility: Requires openspec CLI.
metadata:
  author: netclaw
  version: "0.1"
---

# Netclaw OpenSpec Planning Skill

Use this skill when working on planning artifacts for Netclaw.

## Intent

Create or update OpenSpec change artifacts that are explicitly traceable to
`docs/prd/` and `docs/spec/`.

## Required Inputs

- change name (kebab-case)
- source PRD IDs
- capability specs affected under `openspec/specs/`

## Workflow

1. Validate planning baseline exists:
   - `docs/prd/README.md`
   - `docs/spec/README.md`
   - `openspec/config.yaml`
2. Create or continue change:
   - `openspec new change <name>` (if missing)
3. Author planning artifacts:
   - `proposal.md` with `Source PRDs`
   - `design.md` with goals/non-goals and key decisions
   - `tasks.md` with verifiable checklist items
   - delta specs in `openspec/changes/<name>/specs/*/spec.md`
4. Validate consistency:
   - PRD -> engineering spec -> OpenSpec capability alignment
5. Keep scope explicit:
   - separate MVP-now from deferred work

## Guardrails

- Do not implement production code in planning-only changes.
- Include security and operational impact in proposal/design when relevant.
- Keep requirements testable with scenario format.

## Done Criteria

- change contains proposal, design, tasks, and delta specs
- source PRDs are listed
- delta specs align with existing capability specs and MVP boundaries
