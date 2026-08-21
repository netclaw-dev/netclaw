## Context

The current core exposes general workspace tools but defers `attach_file`, even though attaching an existing authorized file is a small and frequent operation. An agent that does not see the schema may copy the file into session scratch with shell before it discovers the attachment tool. Separately, agents sometimes put a known Netclaw tool name in `shell_execute`; the authorization pipeline then treats it as an ordinary process request and can prompt the user.

Netclaw already has the necessary boundaries:

- ShellSyntaxTree supplies parser-owned command occurrences and exact authored verb tokens.
- `ToolRegistry` owns first-party registrations and Core/Deferred exposure.
- `ToolAccessPolicy.IsToolExposed` applies deployment, audience, and approval-policy visibility.
- `ToolAuthorizationDecision` is the internal pre-execution result.
- parent and child actors already activate deferred tools in actor-local caches.
- typed `ToolInvocationReceipt` remediation reaches both actor paths without entering durable history.

The design must not add executable-specific parsing to Netclaw, infer native arguments from shell text, or make schema exposure equivalent to authorization.

## Goals / Non-Goals

**Goals:**

- Make direct attachment of an authorized existing file obvious and available without a discovery round trip.
- Stop complete static shell calls that contain an exact first-party Netclaw executable name before shell policy can approve, prompt, or execute them.
- Give the next model call the exact policy-visible native schema needed to recover.
- Preserve identical parent and child behavior and preserve ordinary authorization on the eventual native call.
- Pin the behavior with deterministic tests and PII-free behavioral evidence.

**Non-Goals:**

- Parse or translate native-tool arguments from shell arguments.
- Fuzzy-match, alias-resolve, or guess an intended tool.
- Intercept dynamic or unresolved executable identities.
- Correct MCP names or `shell_execute` itself.
- Persist loaded schemas, corrections, or new durable state.
- Automatically retry or execute any tool.

## Decisions

### Add `attach_file` to the policy-filtered parent-session core

`ToolRegistrationExtensions` will register `attach_file` as Core. Parent sessions will receive its schema when current policy exposes it. Its description will state that the caller passes the authorized source path directly and Netclaw performs any required safe copy into the current session. Existing `ScopedFileAccessPolicy`, proximity checks, and `ToolPathPolicy` remain unchanged.

Sub-agents will exclude `attach_file` from core exposure, discovery, loading, and direct dispatch. A child tool context can create an attachment, but the current child completion path has no internal typed handoff that can carry that attachment to the parent invocation and channel. Exclusion prevents a false-success result without adding attachment state to the public `SubAgentResult` contract. A later change can remove the exclusion after it adds an internal child-to-parent attachment handoff.

This adds one small schema to parent sessions but removes a discovery round trip and the misleading incentive to use shell copy first. Audience policy still removes the tool when it is unavailable.

**Alternative considered:** keep the tool Deferred and strengthen prose. The observed failure is caused by the missing affordance at choice time; more prose does not provide a callable schema and has already proved unreliable.

### Detect exact authored executable identity from ShellSyntaxTree facts

After ordinary shell argument validation, audience checks, protected-path checks, and analysis succeed, but before stored-grant matching, approval, or execution, the dispatcher will scan the parser-owned command occurrences in source order. A match requires:

- the complete analysis is resolved and contains no dynamic syntax;
- the occurrence is complete and its authored verb is static;
- the first authored verb token exactly equals a registered first-party tool name using ordinal comparison;
- the target is not `shell_execute`, is not an MCP adapter, and is exposed by `ToolAccessPolicy` for the active invocation.

Arguments, redirects, pipelines, and surrounding static compound structure are not interpreted as native syntax. If a static occurrence matches, the entire shell call is stopped. The first matching occurrence supplies the recovery target. A protected-path or other terminal preflight denial wins before this check.

**Alternative considered:** recognize only a bare one-token command. That misses the observed class where the model types a native tool name with prose-like arguments or includes it in a compound shell call. Matching only the executable token uses general shell facts without learning any tool's private grammar.

**Alternative considered:** search raw command text. This would create quoting, comment, alias, and injection errors and would duplicate ShellSyntaxTree.

### Represent correction explicitly through the authorization boundary

Add an internal `RequiresAgentCorrection` authorization outcome carrying `ToolAgentCorrection.NativeToolSuggested`. The dispatcher adapter converts it to a dedicated internal correction exception for the existing async execution boundary. It does not represent allow, deny, or approval and cannot contain approval matches.

Parent and child catch the correction, return a `RecoverableCorrection` receipt with the new closed `UseNativeTool` code, and include a separate typed actor-local exposure request containing the canonical tool name. The shared remediation presenter appends one fixed instruction; it does not parse result text or interpolate untrusted shell arguments.

**Alternative considered:** encode the target in a free-form remediation string. That would recreate the weak string contract removed by the lower stack and would encourage actor code to parse model-facing text.

### Activate schema only after the correction result enters actor history

The internal tool-result messages will carry exposure requests separately from durable chat messages and receipts. The parent or child actor first records the correction result, then asks its existing actor-local activation function to load the target. Activation repeats registry and `ToolAccessPolicy` checks. Missing, removed, or newly hidden tools fail closed.

Core targets make activation a no-op. Deferred targets appear on the next model request. Recovery, model failure, child termination, and session restart keep the existing actor-local eviction behavior.

### Keep normal native authorization unchanged

The correction does not call the target, seed a one-time approval, create a grant, or bypass tool policy. When the model retries with the native tool, dispatch starts again at ordinary argument validation, audience policy, approval mode, and execution.

## Risks / Trade-offs

- **[Name collision with a real host executable]** A first-party tool name could also exist on PATH. → Only policy-visible Netclaw names are intercepted, exact ordinal authored identity is required, and the behavior is deliberate: model calls should use the structured tool when both names collide.
- **[A compound shell call is stopped before unrelated commands run]** → The result states that the entire shell call was not executed; partial execution would be unsafe and surprising.
- **[Core schema growth]** → Add only `attach_file`, refresh the exact core snapshot and byte footprint, and measure hosted behavior before delivery.
- **[Policy changes between detection and activation]** → Repeat policy validation at actor activation and again at eventual dispatch.
- **[Correction loop]** → The model receives the actual schema on the next request. Repeating the same shell mistake remains a correction and never becomes shell authority.

## Migration Plan

No durable migration is required. Deploy the actor and tool-registration changes together. Existing sessions rebuild the policy-filtered core on their next actor/model lifecycle; deferred activation remains transient. Rollback restores the prior core and shell behavior without data conversion.

## Open Questions

None. Hosted evidence may reject the behavioral hypothesis, but it does not change the security contract: exact native-tool shell mistakes must not become shell executions or approval prompts.
