# SPEC-007: Guided Onboarding Experience

Source PRDs: `PRD-004`, `PRD-002`, `PRD-005`

## Purpose

Define the guided onboarding flow for first-time Netclaw setup.

## Entry Points

- `netclaw init` (interactive default)
- `netclaw init --resume`
- `netclaw init --non-interactive ...` for automation

## Onboarding Steps

### Step 1: Environment Check

- verify required runtime version
- verify writable config path
- detect existing partial setup

### Step 2: Slack Setup

- collect and validate `SLACK_BOT_TOKEN`
- collect and validate `SLACK_APP_TOKEN`
- test Socket Mode connectivity

### Step 3: Provider Setup

- default selection: OpenRouter
- collect provider credentials and default model
- validate provider authentication with dry-run request

### Step 4: ACL Bootstrap

- create default-deny ACL template
- capture owner identifiers and allowed channels
- set mention/ambient behavior per channel

### Step 5: Security Profile

- choose exposure mode (`local`, `reverse-proxy`, `tailscale-serve`,
  `tailscale-funnel`, `cloudflare-tunnel`)
- for `reverse-proxy`: collect `Daemon.Host` (must be non-loopback) and
  `Daemon.TrustedProxies` (≥1 IP or CIDR entry required to advance — matches
  the daemon's startup validator so the wizard cannot emit a non-startable
  config), then show an informational notice with the resulting serving URL
  (`http://{Host}:{Port}`) before continuing
- enforce policy prerequisites for selected mode

### Step 6: Final Validation

- run config and ACL validation
- show summary with red/yellow/green status
- output next run commands

## Resume Behavior

- incomplete steps are persisted with status
- resumed onboarding starts at first incomplete step
- validated completed steps can be skipped with explicit confirmation

## Safety Requirements

- secrets are never echoed in plain text
- risky internet-reachable exposure modes require explicit confirmation text
- audience/posture choice and exposure mode remain separate decisions
- onboarding must fail closed if validation fails
