# Design: Provider Smoke and CI Independence

## Context

OpenRouter is default, but local development may prefer Ollama. We need a test
strategy that supports both local realism and CI reliability.

## Goals / Non-Goals

Goals:

- define explicit split between required CI tests and optional live smoke tests
- define smoke command contract for developer workflows

Non-goals:

- implementing all test harnesses in this planning change

## Decisions

### Decision 1: CI-required tests use fakes/mocks only

Required CI jobs do not call live providers.

### Decision 2: Live smoke tests are explicit opt-in

Live provider checks run only through explicit command invocation.

### Decision 3: Ollama support is endpoint-driven

Ollama is treated as an OpenAI-compatible provider profile for smoke checks.

### Decision 4: Local smoke defaults target big-gpu

Default local smoke profile points at `http://big-gpu:11434` with
`qwen3:30b` as preferred model and `qwen3:14b` as fallback.
