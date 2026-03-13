# Netclaw.OpenAICompatible

This project contains Netclaw's raw HTTP client for OpenAI-compatible servers.

Why this exists:

- Some local/self-hosted runtimes expose an OpenAI-style API surface but are not
  fully compatible with the official OpenAI .NET SDK request/stream semantics.
- Netclaw needs a provider path that can target the officially documented API
  contract of servers like Lemonade without depending on SDK-specific behavior.
- We still want the rest of Netclaw to program against `Microsoft.Extensions.AI`
  abstractions, especially `IChatClient`.

What belongs here:

- request/response DTOs for the supported OpenAI-compatible subset
- raw HTTP request construction
- SSE streaming parsing
- tool call serialization/parsing
- compatibility shims for documented vendor behavior

What does not belong here:

- provider registration and DI wiring
- app-specific session orchestration
- OpenAI-hosted or OpenRouter-specific logic already covered by the official SDK

Current target contract:

- Lemonade's documented `/api/v1` OpenAI-compatible endpoints
- nearby servers with a similar documented OpenAI-compatible subset, such as
  vLLM-style chat endpoints, where the official SDK may be too strict or too
  opinionated for interoperability

Testing approach:

- integration-style tests should use a local mock HTTP server that reproduces the
  official documented request/response spectrum
- do not hit live inference servers in tests
