# ADR-001: Own Tool Schemas via Compile-Time Source Generation

**Date:** 2026-02-22
**Status:** Accepted
**Context:** First-party tool integration (shell, file read, file write)

## Decision

Netclaw owns its tool schema pipeline via a Roslyn incremental source generator.
Tool authors declare a typed parameter record and an `ExecuteAsync` method; the
generator emits JSON schema and a typed argument deserializer at compile time.

At the `ChatOptions.Tools` boundary, schemas are wrapped in
`AIFunctionFactory.CreateDeclaration()` to produce `AITool` objects that any
`IChatClient` implementation can consume.

## Context

When we first wired tools to a real LLM (Ollama/qwen3:30b via OllamaSharp), two
bugs surfaced immediately:

1. **Nullable schema incompatibility.** `AIFunctionFactory` reflected over
   `string? working_directory = null` and emitted `"type": ["string", "null"]`
   in the JSON Schema. OllamaSharp's `AbstractionMapper` expected `"type"` to be
   a plain string and threw a `JsonException`.

2. **Argument type mismatch.** `FunctionCallContent.Arguments` is typed as
   `IDictionary<string, object?>`, but the actual values inside depend on the
   provider. OllamaSharp puts `JsonElement` values; other providers may put
   native strings or ints. Our tools did `cmdObj is not string` and failed on
   every call.

Both bugs exist in the seam between schema generation (framework-owned) and
argument extraction (our code).

### Why M.E.AI's Approach Fails Structurally

**M.E.AI generates the schema but doesn't own the consumption.** It reflects
over your method, emits valid JSON Schema (`["string", "null"]`), then hands it
to the provider SDK. The provider SDK re-parses that schema to convert it to the
LLM's native tool format. If the provider's parser doesn't handle all JSON
Schema patterns that M.E.AI emits, you get a runtime error. Nobody owns the full
path — M.E.AI generates, the provider consumes, and they disagree on which
subset of JSON Schema is valid.

**M.E.AI doesn't normalize arguments on the return trip.** The `Arguments` dict
is `IDictionary<string, object?>` where the `object?` is whatever the provider
SDK puts there. If you let M.E.AI invoke the function end-to-end, it handles
binding internally. But the moment you dispatch manually — because you need
injected dependencies, audit logging, policy checks — you're on your own for
type coercion.

**M.E.AI is an abstraction layer, not an implementation layer.** It defines
`IChatClient` and `AITool` but doesn't own the behavior behind them. When two
implementations disagree across provider boundaries, there's no single owner to
fix it.

### Agent SDK Landscape

| Approach | Schema | Invocation | Who |
|----------|--------|------------|-----|
| Own the pipeline | Hand-written JSON or generated | Parse arguments yourself | Oinker, IronClaw, most Go/Rust agents |
| Framework does everything | Reflected from methods | Framework binds typed args | Semantic Kernel, LangChain |
| Split responsibilities | Framework generates schema | Manual dispatch with raw dict | Where Netclaw started |

Approach 3 is unstable: the framework generates schemas you don't control, and
providers deserialize arguments in ways the framework doesn't normalize.

Hand-written JSON schemas (Approach 1) fix the control problem but create a DX
problem: schemas and extraction logic are separate, error-prone, and tedious.

## Rationale

**Build to the problem, not to the tool.**

A Roslyn incremental source generator gives the DX of Approach 2 (declare a
real function, everything else derived) with the control of Approach 1 (we own
every mapping rule):

- **Schema generation** happens at compile time from typed parameter records.
  We control the rules: `string?` emits `"type": "string"` (not
  `["string", "null"]`), nullability is expressed via the `"required"` list.
- **Argument deserialization** is generated per-tool. Handles `JsonElement` and
  native CLR types from any provider, producing a typed params object.
- **Single owner.** We own both ends of the pipeline. The provider SDK is a
  black box in the middle, but we defensively handle whatever it produces on
  both sides.
- **Same DX as AIFunctionFactory** — tool authors write typed parameters and an
  execute method. No hand-written JSON, no phantom `Describe` methods, no
  runtime reflection.
- **Source generator is replaceable.** If M.E.AI fixes these issues upstream, we
  can swap the generator internals without changing any tool author code.

### Tool Author DX

```csharp
[NetclawTool("shell_execute",
    "Execute a shell command and return stdout/stderr with exit code",
    Grant = "shell")]
public partial class ShellTool : NetclawTool<ShellTool.Params>
{
    public record Params(
        [Description("The shell command to execute")] string Command,
        [Description("Working directory (optional)")] string? WorkingDirectory = null);

    protected override Task<string> ExecuteAsync(Params args, CancellationToken ct)
    {
        // args.Command — typed, non-null
        // args.WorkingDirectory — typed, nullable
    }
}
```

Registration: `registry.Register(new ShellTool(config))`.

### What the Generator Emits

For each `NetclawTool<TParams>` subclass:

1. Static `JsonElement` field with the JSON schema
2. `Parse(IDictionary<string, object?>)` method for typed deserialization
3. `INetclawTool` interface implementation (Name, Description, ParameterSchema,
   GrantCategory, untyped ExecuteAsync dispatch)

## Consequences

- Tool definition is single-source: the parameter record is the schema. Adding
  a parameter means adding a constructor parameter — one place, not two.
- `IChatClient` remains the LLM transport abstraction. `AITool` objects are
  produced at the boundary via `CreateDeclaration`.
- Runtime reflection for tool schemas is eliminated. Schema is a compiled
  literal.
- The source generator project is an additional build dependency. Incremental
  generation ensures it doesn't impact build times significantly.
- Tests using `AIFunctionFactory.Create(() => "result", name)` for fakes
  continue to work alongside real tools.

## Alternatives Considered

**Hand-written JSON schemas.** Fixes control but bad DX — schema and extraction
are separate, must be kept in sync manually. Acceptable as a stepping stone,
not as the final state.

**Runtime reflection in a base class.** Same DX as the source generator but
at runtime. Works, but reintroduces the same category of problem we're solving
(reflection producing unexpected output). Also adds startup cost.

**Let M.E.AI invoke tools directly (Approach 2).** Requires restructuring tools
to capture dependencies in closures. Still depends on framework schema
generation. Trades one framework dependency for another.

**Abandon `AITool` entirely.** Requires custom tool types and per-provider
serialization. Rejected because `ChatOptions.Tools` expects `AITool` and
replacing that means replacing `IChatClient`.
