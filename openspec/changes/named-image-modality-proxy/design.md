## Context

Named model definitions own model metadata, but the resolver now discards their names.
The daemon builds clients only for the role assignments.
Session media records preserve original image files across actor recovery.

A text-only main model cannot consume those image records.
The proxy must create text without the full model-router design from issue `#648`.

## Goals / Non-Goals

**Goals:**

- Resolve any named model definition through one runtime registry.
- Reuse the current provider client factory and capability resolver.
- Let one named image proxy create a durable description.
- Preserve the original image as the authoritative session media.
- Support new attachments and old session images.
- Add fail-closed CLI, TUI, schema, and startup validation.

**Non-Goals:**

- Add audio or video proxies.
- Add subagent model assignments.
- Add per-turn route policy or load balance.
- Send session history or tools to the proxy.
- Add image crop or follow-up analysis tools.

## Decisions

### Extend the canonical named model shape

`NamedModelConfiguration` will add `Proxies.Image` as an optional definition name.
The resolver will retain a case-insensitive copy of all named definitions and assignments.
Legacy inline role configuration will continue to work without a proxy.

The CLI will migrate legacy configuration before it writes an image proxy.
It will reuse a matching definition and preserve operator metadata.

An independent provider and model pair under `Proxies` was rejected.
That shape would duplicate model identity and metadata.

### Add one named model runtime registry

The daemon registry will map each definition name to its `ModelReference`.
The registry will create and cache one composed `IChatClient` for each used definition.
It will resolve and cache the effective capabilities for the same definition.

The current role provider will use the registry through role-to-name assignments.
This keeps the actor role API and prepares one explicit-name seam for later work.

An unknown definition or an invalid proxy capability will fail at startup.
The image proxy must accept image input and produce text output.

### Keep proxy work behind an actor service

The session actor will ask an `IImageProxyAnalyzer` for one image description.
The service will use the named registry and a fixed versioned prompt.
It will send one image, no session history, and no tools.

The service will reject an empty result.
It will neutralize its own output delimiter before it returns the text.

### Persist proxy results as session events

A session event will record these fields:

- the session-relative source path
- the proxy definition name
- the proxy model ID
- the prompt version
- the description
- the UTC timestamp in Unix milliseconds

The actor will persist this event before it calls the main model.
The snapshot will include the same records.
Recovery will rebuild the result map without a new proxy call.

The actor will request a result on demand when old history has no result.
One actor command will analyze one image at a time to keep actor state explicit.

### Select original media or derived text at assembly time

The message assembler will receive the active main modalities and the durable result map.
It will restore the original image when the main model accepts image input.
It will insert the stored description when the main model accepts text only.

The inserted text will identify the session-relative path.
It will state that the proxy output is untrusted user content.
The original media reference will remain unchanged.

### Retain image attachments when a proxy exists

The channel attachment decision will treat a configured image proxy as an image-input route.
The adapter will preserve the current attachment policy and media store path.
Its canonical attachment line will identify `via="image-proxy"`.

The adapter will not call the proxy.
The session actor remains the only owner of analysis and durable state.

## Risks / Trade-offs

- [Proxy text can contain prompt-control text] -> The wrapper labels it as untrusted and neutralizes its delimiter.
- [A proxy call adds latency] -> The actor calls it only for an image with no durable result.
- [A proxy model changes later] -> Existing results remain durable and include the original proxy identity.
- [A historical session has many images] -> The actor processes them in order and persists each result.
- [A proxy fails] -> The actor stops the turn and does not call the main model.
- [A text-only model has no proxy] -> The compatibility error from issue `#1727` remains visible.

## Migration Plan

The new property is optional.
Existing named and legacy configurations keep their current runtime behavior.
`netclaw model set image-proxy` converts a legacy model section to the named shape before it writes the assignment.

The schema accepts `Proxies` only in the named shape.
Rollback requires removal of `Models.Proxies` before an older binary reads the configuration.
Durable proxy events remain harmless session data after rollback.

## Open Questions

None.
