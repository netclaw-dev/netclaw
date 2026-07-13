# SPEC-007: Guided Onboarding Experience

Source PRDs: `PRD-004`, `PRD-002`, `PRD-005`

## Purpose

Define the bootstrap-first `netclaw init` experience and its limited
existing-install re-entry paths.

## Entry Points

- `netclaw init` (interactive default)
- `netclaw init --non-interactive ...` for automation

## Fresh-Install Flow

### Step 1: Provider Setup

- select provider type
- collect credentials or OAuth device flow inputs
- assign the initial model
- validate provider authentication and connectivity

### Step 2: Identity

- collect workspaces directory
- collect user name
- collect timezone
- regenerate `SOUL.md` and `TOOLING.md`
- seed a minimal deployment `AGENTS.md` scaffold only when absent

### Step 3: Security Posture

- choose `Personal`, `Team`, or `Public`
- keep posture distinct from both Enabled Features and Audience Profiles

### Step 4: Enabled Features

- shown automatically for `Team` and `Public`
- skipped for `Personal`
- controls deployment-wide runtime enablement only

### Step 5: Final Validation

- automatically run config and health validation when the step is reached
- show summary with remediation guidance on failure
- output next-step commands (`netclaw chat`, `netclaw config`)

## Existing-Install Flow

When an install already exists, `netclaw init` SHALL NOT replay the full
bootstrap flow by default. Instead it presents:

1. `Redo identity setup`
2. `Open configuration editor`
3. `Start over from scratch`
4. `Cancel`

### Identity Re-entry

- remains init-owned
- reuses the identity form with existing values prefilled
- continues into the bot-assisted identity conversation
- never overwrites an existing deployment `AGENTS.md`

### Post-Init Conversation

- discovers operator/personality context for `SOUL.md`
- discovers mission, workflows, required skills, delegation, and review gates
  for `AGENTS.md`
- summarizes the proposed playbook and requires confirmation before writing
- reads and preserves existing identity-file content
- reports that confirmed changes apply on the next inbound message

### Start Over From Scratch

- opens a second dialog with:
  - `Reset setup only`
  - `Full reset`
  - `Cancel`
- both destructive options require double confirmation

`Reset setup only` preserves working data such as the SQLite database, logs,
projects, schedules, environment, and skills.

`Full reset` wipes the entire Netclaw home except the binary payload.

## Safety Requirements

- secrets are never echoed in plain text
- structurally invalid config blocks save without override
- runtime/probe failures may offer explicit `Save anyway`
- posture, Enabled Features, and Audience Profiles remain separate decisions
- onboarding must fail closed if validation fails
- `netclaw init --force` is not part of this flow
