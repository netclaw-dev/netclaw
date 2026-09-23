## Context

See `proposal.md` for the reason for this change. PR #2180 now projects multi-part MCP results into readable text and safe markers.

The formatter discards artifact bytes after it creates those markers. The daemon already receives a `ToolInvocationContext` with session storage, model modalities, and `ToolExecutionOutputs`.

The existing scanner verifies bytes and returns `VerifiedMimeType`. The tool pipeline already checks model MIME, size, magic bytes, and active model modalities.

## Goals / Non-Goals

**Goals:**

- Walk each MCP result once for text and artifact candidates.
- Scan each candidate before any write.
- Reuse session artifacts and both existing tool-output sinks.
- Preserve text when one artifact fails.
- Keep model compatibility checks at the provider boundary.

**Non-Goals:**

- Do not add a general media framework.
- Do not route MCP output through channel ingress code.
- Do not expand scanner MIME support.
- Do not add configuration or a serialized contract.

## Decisions

### Use one projection for every MCP SDK result shape

`McpToolResultFormatter.Project` returns model text, error state, error detail, artifact candidates, and safe projection notes.

It accepts the current `AIContent`, `AIContent[]`, and `JsonElement` envelope shapes. Existing format methods remain text-only compatibility wrappers.

The projection enforces the shared count and byte limits before it copies inline data into an immutable candidate.
The candidate carries only bytes, the declared MIME, and an optional name.

`DataContent` permits mutable metadata. JSON envelopes also expose bytes without
a `DataContent` object. The candidate gives both SDK shapes one immutable,
call-local snapshot. It is not a second media pipeline or durable model.

Alternative: Parse the result again in the daemon. This duplicates protocol shape rules and can make text and artifact order differ.

### Put MCP artifact admission beside the daemon MCP manager

One internal materializer owns scan calls, artifact writes, and output registration. It receives the existing scanner through dependency injection.

The materializer receives session paths and model modalities from `ToolInvocationContext`. It adds no root, policy, or configuration dependency.

Alternative: Reuse `AttachmentIngressPipeline`. That path owns channel downloads, audience attachment policy, and channel rejection replies.

### Keep decision ownership explicit

| Decision | Owner | State lifetime |
|---|---|---|
| MCP result shape, content order, and copy limits | `McpToolResultFormatter` | Call-local |
| MCP scan-before-write order | MCP artifact materializer | Call-local |
| Verified MIME | Existing `IContentScanner` | Call-local |
| Stored artifact path | Existing `SessionStoragePaths.ArtifactDirectory` | Durable file |
| User and model output registration | Existing `ToolExecutionOutputs` | Call-local |
| Model compatibility backstop | Existing session tool pipeline | Actor-local turn state |
| User file delivery | Existing session subscriber path | Call-local delivery |

No actor receives an MCP-specific message. No new state enters the journal or a snapshot.

### Use the existing policy seams in order

The following flow is schematic. It includes each security and modality gate.

```text
MCP SDK result
    |
    v
project text and enforce copy limits for artifact candidates
    |
    v
scan bytes with declared MIME and a safe provisional name
    |
    v
require scanner-verified MIME
    |
    v
write a create-new file below ArtifactDirectory
    |
    +--> AddFileAttachment --> existing FileOutput path --> user
    |
    +--> catalog and active-model check
             |
             +--> AddModelInputFile --> existing provider-boundary checks
```

The server name and MIME claim can inform the scan. Only the verified MIME selects the final extension and output MIME.

### Register outputs after all artifact work finishes

The materializer keeps created files and accepted outputs in call-local lists. It registers outputs only after it processes all candidates.

Caller cancellation removes files from the call and rethrows cancellation. A normal per-artifact failure keeps other accepted artifacts and readable text.

This approach prevents a cancelled call from publishing a partial output set. It does not make retained files transactional across a process crash.

### Deliver every verified artifact to the user

Every accepted artifact uses `AddFileAttachment`. An eligible artifact also uses `AddModelInputFile` when the active model supports its modality.

`AttachmentInlineDecision` and `MimeTypeCatalog` make the early model decision. The session tool pipeline repeats the provider-boundary checks.

Alternative: Send a file only when the model cannot inspect it. That option can hide the original tool product from the user.

### Bound post-SDK work without a new setting

The projection accepts at most ten candidates and 25 MiB of aggregate candidate bytes. These values match current session attachment ceilings.

The projection is the sole source of candidates for the materializer. Its internal candidate constructor prevents a second production creation path.

The bounds limit scan and file work after the SDK returns. They cannot limit memory that the SDK already used for the response.

## Risks / Trade-offs

- [Risk] The SDK can materialize a large response before Netclaw sees it. → Keep explicit post-SDK limits and document this limit.
- [Risk] File outputs can add channel noise. → Deliver the original artifact because silent loss is worse than the extra file.
- [Risk] A process crash can leave an unregistered artifact file. → Use unique create-new names inside the session artifact directory.
- [Risk] Scanner support is narrower than MCP media types. → Reject unsupported types visibly and do not add a fallback.
- [Risk] A future tool can register incompatible model media. → Keep the existing provider-boundary checks unchanged.

## Migration Plan

1. Deploy the daemon with the new projection and materializer.
2. Restart the daemon to activate the new MCP result path.
3. Keep existing configuration and persisted actor state unchanged.

A source revert removes future artifact delivery. It does not remove files from prior successful calls.
