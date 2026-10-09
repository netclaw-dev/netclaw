# Deployment Mission and Operating Playbook

This file describes how this Netclaw deployment performs its job. Keep durable
mission guidance, recurring workflows, skill-selection rules, delegation
practices, and quality checks here.

Do not store secrets, credentials, private customer data, or other audience-
sensitive facts in this file. The same playbook can guide Personal, Team, and
Public conversations and the sub-agents they launch.

## Mission and Desired Outcomes

<!-- What function does this agent perform, for whom, and what does success look like? -->

## Recurring Workflows

<!-- Describe repeatable steps for important tasks. -->

## Skill Selection

<!-- Which skills should be loaded for each kind of work? -->

## Delegation

Use children for useful independent work, including substantial code tasks, when their profiles permit that work.
Give each child a precise objective, authorized workspace, output artifact, and acceptance checks.
Assign one writer per workspace. Concurrent writers need separate authorized worktrees.
Review each returned artifact and its evidence before delivery.

`spawn_agent` returns durable acceptance before the child finishes. Continue independent parent work.
The terminal result arrives later. Load `check_agent_run` through `load_tool` for status or cancellation.
Use its exact `log_path` with normal file tools for child diagnosis. A log does not prove current health or completion.
Cancellation acceptance does not prove dispatch closure. Read the closure facts and retained partial evidence.
Use cancellation and a revised task for new instructions. Live steering and private agent messages are unavailable.
Load `netclaw-operations`, then read `references/child-runs.md` for the full lifecycle rules.

## Review and Quality Gates

<!-- What must be checked, revised, or approved before work is delivered? -->

## Organizational Conventions

<!-- Add durable terminology, etiquette, formatting, or process conventions. -->
