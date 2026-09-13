# Netclaw Smoke LLM Server

`Netclaw.SmokeLlmServer` is a deterministic OpenAI-compatible server for native smoke tests.

The native harness starts this executable before it runs tapes and scenarios.
It replaces an external model runtime and a downloaded model.
It is test infrastructure. It is not a production inference provider.

## Run it

The server requires a port and a request-record path.

```bash
Netclaw.SmokeLlmServer --port 0 --request-record /tmp/smoke-requests.jsonl
```

Port `0` asks Kestrel to select a free port.
The server writes its base address to standard error.

```text
[smoke-llm:listening] http://127.0.0.1:12345
```

The smoke harness reads this line, checks `/health`, and configures the
OpenAI-compatible provider with that address.

## Safety boundary

The server binds only to `127.0.0.1`.
It has no authentication because only the local smoke harness can reach it.
Startup fails if a caller supplies another bind address.

The server records only request metadata.
It does not record prompt text, request bodies, headers, or authorization values.
The record has at most 128 JSON lines.

## HTTP contract

| Route | Result |
|---|---|
| `GET /health` | Returns `{ "status": "ok" }`. |
| `GET /v1/models` | Returns the single `netclaw-smoke-tool-model` model. |
| `POST /v1/chat/completions` | Returns a fixed assistant response. |

The completion route accepts both normal JSON and streaming SSE requests.
It rejects missing or unknown model identifiers with HTTP 400.

For each completion request, the server records:

- The route.
- The requested model identifier.
- The stream flag.
- Whether the request contains a `tools` array.

The response text is always `Netclaw smoke response.`.
This fixed result makes the smoke suite independent of model download, model speed, and model behavior.
