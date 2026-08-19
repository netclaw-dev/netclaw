## Context

The `openai-compatible` provider type (`OpenAiCompatibleDescriptor`,
TypeKey `openai-compatible`) targets self-hosted OpenAI-shaped backends. Its
transport already supports auth: `OpenAiCompatibleChatClient.BuildRequest`
sets `Authorization: Bearer` when `ApiKey` is present, and the probe, models
client, and capability resolver do the same. The CLI
(`netclaw provider add <name> openai-compatible --endpoint <url> --api-key <key>`)
writes the key to encrypted secrets today.

The gap is the auth declaration. `Auth` is `EndpointOnlyAuth`
(`SupportedAuthMethods = [AuthMethod.None]`), and the TUI drives off that:
- The wizard skips the auth sub-step when the method set is `[None]`.
- `OAuthFlowViews.BuildAuthMethodLabels` filters out `AuthMethod.None`.
- `ProviderStepView.BuildCredentialInput` and
  `ProviderManagerPage.BuildCredentialsView` switch on the concrete auth type:
  `EndpointOnlyAuth` shows endpoint only; every other shape shows API-key
  only. No shape today means "endpoint plus optional key".

So an operator cannot reach the already-working auth path from any
interactive surface, and the declared contract misdescribes the runtime.

## Goals / Non-Goals

**Goals:**

- The `openai-compatible` auth method set is `[None, ApiKey]`.
- The wizard and the provider manager offer both auth choices with an
  explicit "No auth" label for `None`.
- Credential screens show endpoint input plus an optional API-key input for
  this shape.
- An entered key is probed with Bearer, persisted to encrypted secrets, and
  recorded as `AuthMethod.ApiKey`; an empty key behaves exactly as today.
- Existing no-auth configurations are untouched in behavior.

**Non-Goals:**

- No transport change — the wire paths already send Bearer when a key exists.
- No non-Bearer header schemes (`api-key`, `x-api-key`, Azure-style).
- No per-instance display names, no new type keys, no OAuth for this shape.
- No config schema change (`Providers` is schema-open).

## Decisions

### D1: One new auth shape, not an extended `EndpointOnlyAuth`

Add `EndpointOrApiKeyAuth : IProviderAuth` with
`SupportedAuthMethods = [AuthMethod.None, AuthMethod.ApiKey]`.

Rationale: the TUI switches on concrete auth types
(`IProviderAuth` doc comment states this contract). Changing
`EndpointOnlyAuth` to carry two methods would flip every existing
endpoint-only consumer — including Ollama — into new UI paths. A distinct
shape confines the change to `openai-compatible`.
_Alternative rejected:_ making `EndpointOnlyAuth.SupportApiKey` configurable —
same concrete-type switch, but with hidden state that the TUI must also
switch on; two axes where one type each suffices.

### D2: Method order — `None` first

`SupportedAuthMethods = [None, ApiKey]` keeps "No auth" as the default
selection wherever the picker defaults to index 0. Local backends stay the
common case; auth is opt-in per instance.

### D3: `None` gets an explicit auth-picker label

`BuildAuthMethodLabels` currently drops `AuthMethod.None` because no
multi-method provider offered it. For this shape the wizard shows an auth
picker with two labeled choices; the label for `None` is "No auth (local
endpoint)". Selection drives which credential fields appear and which
`AuthMethod` is persisted. This also preserves the existing skip behavior:
single-`None` providers (Ollama) still bypass the picker entirely.

### D4: Optional key input, not two sequential screens

The credential screen for this shape shows the endpoint input first (Enter
advances), then the API-key input where an empty submit means "no key".
Empty submit stores no secret and persists `AuthMethod.None`. A non-empty
key persists `AuthMethod.ApiKey` and writes the secret.

Rationale: one screen with a clear skip matches the "optional" contract and
avoids a modal question before every field.

### D5: No new validation gate in the daemon

Startup tri-state validation stays as is: a provider entry with
`AuthMethod.ApiKey` and a missing key must fail visibly through the existing
per-descriptor credential check (`ChatClientDoctorCheck.MissingCredentialMessage`),
not a new validator. The descriptor's `Auth` shape already declares that
`ApiKey` is one supported method, and the existing doctor logic handles
method/credential mismatch.

## Risks / Trade-offs

- **Concrete-type switches in TUI** — two views branch on the auth type today;
  this adds a third branch in each. Accepted: the `IProviderAuth` contract
  documents the switch. A generic field-driven auth model would be a larger
  refactor with no additional behavior.
- **Smoke tape churn** — the wizard and provider-manager tapes drive the
  provider flow by list index; a new auth sub-step changes the keystroke
  sequence. Mitigation: update `init-wizard.tape` and `provider-add.tape`
  in the same PR and run the light smoke suite.
- **Wrong method/credential combinations via hand-edited config** (for
  example `AuthMethod: ApiKey` with no key) — pre-existing behavior; doctor
  reports it. Not made worse by this change; covered by a fake-failure test
  at the TUI save boundary.

## Open Questions

None — design decisions confirmed with the operator during planning:
extend `openai-compatible` (no new type key), optional key, Bearer only.
