# Proposal: Add Guided Onboarding and Provider Strategy

## Source PRDs

- `PRD-004-cli-onboarding-and-config.md`
- `PRD-005-model-provider-strategy.md`

## Why

Successful first-run setup is a core product requirement. Users should be able
to configure Slack + model provider safely without manual file editing.

## What Changes

1. Define guided onboarding flow requirements and step sequencing.
2. Define provider abstraction requirements with OpenRouter as default.
3. Add diagnostics and validation requirements for provider health and config.

## Scope

In scope:

- OpenSpec capability updates for onboarding and providers
- planning artifacts only

Out of scope:

- implementing provider clients and onboarding command handlers
- advanced failover routing between providers

## Impact

- lowers setup friction for initial deployment
- preserves future extensibility for multiple providers
- improves operational confidence through validation and diagnostics
