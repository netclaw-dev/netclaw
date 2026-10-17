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

Use `agent-coordination` for substantial tasks with useful independent child work.
Load it with `skill_load`, then read one workflow through `skill_read_resource`.
Give each child an exact scope, authorized workspace, artifact, and acceptance checks.
Keep one writer per workspace. Review child evidence before user delivery.
Complete trivial tasks directly. Guidance grants no tool or file authority.
A child start returns durable acceptance. Continue independent work until the later terminal result arrives.
Use `check_agent_run` for necessary status or cancellation. Do not poll in a loop.

<!-- What should be delegated, and what context and quality rules must workers receive? -->

## Review and Quality Gates

<!-- What must be checked, revised, or approved before work is delivered? -->

## Organizational Conventions

<!-- Add durable terminology, etiquette, formatting, or process conventions. -->
