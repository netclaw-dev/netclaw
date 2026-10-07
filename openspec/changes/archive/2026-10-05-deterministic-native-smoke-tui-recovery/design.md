## Context

See `proposal.md` for the motivation.
The current native smoke harness owns an external local-model process and a model download.
The harness already owns a deterministic MCP sidecar and its failure artifacts.

## Goals / Non-Goals

**Goals:**

- Use the existing OpenAI-compatible provider path during required native smoke.
- Keep a real process and HTTP boundary.
- Make the result deterministic without a model download or a GPU.
- Fix the independent terminal-error state defect in the Chat TUI.

**Non-Goals:**

- This server does not prove real model quality or general provider compatibility.
- This change does not change tool policy or model routing.

## Decisions

### Use a dedicated published smoke LLM executable

`Netclaw.SmokeLlmServer` will run as a harness-owned child process.
It will bind Kestrel only to `127.0.0.1` and a harness-selected port.
It will expose `/health`, `/v1/models`, and `/v1/chat/completions`.
It will emit fixed OpenAI-compatible JSON or SSE output.

The harness will publish this executable beside the smoke MCP executable.
This keeps startup, process cleanup, and CI artifact ownership explicit.
An in-process mock would not test the real HTTP boundary.

### Use one canonical smoke provider profile

The harness will export `SMOKE_LLM_ENDPOINT` and `SMOKE_LLM_MODEL`.
Tapes and scenarios will configure `openai-compatible` with those exact values.
The config writer is the producer.
Provider discovery and chat completion are the consumers.

The harness will fail when the smoke server lacks health or exits.
It will not fall back to an external local-model runtime.

### Record only safe request metadata

The smoke server will record route, requested model, stream mode, and tool presence.
It will not record headers, prompts, tool schemas, or request bodies.
The harness will copy this record and the server log to failure artifacts.

### Update terminal error state at the view-model boundary

The Chat view model owns `IsGenerating`, pending tools, input availability, and status text.
`ErrorOutput` ends the current generation.
The view model will clear all terminal generation state before it redraws.

## Risks / Trade-offs

- [The fake diverges from the OpenAI wire contract] → HTTP contract tests cover discovery, tools, JSON, and SSE.
- [A child process outlives a run] → The harness tracks the process identifier and stops it in teardown.
- [Artifacts leak data] → The server records an allowlist of metadata fields only.

## Migration Plan

1. Add the smoke LLM executable and its HTTP contract tests.
2. Add harness lifecycle helpers and CI publishing.
3. Convert provider setup, tapes, and scenarios to the canonical smoke profile.
4. Remove external local-model work from required native smoke jobs.
5. Run targeted, native, and repository quality checks.
