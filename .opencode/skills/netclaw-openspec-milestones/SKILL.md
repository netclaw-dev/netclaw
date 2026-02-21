---
name: netclaw-openspec-milestones
description: Build and maintain Netclaw implementation milestones from PRDs/specs/OpenSpec changes.
license: MIT
compatibility: Markdown-only process skill.
metadata:
  author: netclaw
  version: "0.1"
---

# Netclaw OpenSpec Milestones Skill

Use this skill to maintain milestone-oriented implementation plans.

## Artifacts

- `IMPLEMENTATION_PLAN.md` (canonical)
- optional alias file `IMPLEMENTATION_PLAND.md`

## Milestone Construction Rules

For each milestone, include:

1. milestone objective
2. source PRDs
3. source engineering specs
4. source OpenSpec capabilities
5. source OpenSpec changes
6. definition of done (observable)
7. verification method (tests or diagnostics)

## Sequencing Guidance

Recommended order for Netclaw MVP:

1. planning baseline and traceability
2. session + Slack vertical slice
3. security envelope and ACL enforcement
4. guided onboarding + provider strategy
5. operator UX and CLI hardening
6. pi1 acceptance validation

## Guardrails

- maintain MVP scope discipline; defer north-star work explicitly
- never mark milestone complete without verification evidence
- if specs change, update milestone references in the same pull request
