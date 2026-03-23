## Context

`netclaw init` already provides a Termina-based wizard, but provider onboarding is currently optimized for static credential entry and not explicit about OAuth-first paths, model-catalog discovery degradation, or operator recovery after partial setup failures. This change spans onboarding UX (`netclaw-onboarding`), provider behavior contracts (`netclaw-model-providers`), and diagnostics/reporting (`netclaw-cli`) and must remain aligned with PRD-004 and PRD-005.

Architecture constraints remain unchanged: the CLI is thin where possible, runs in .NET 10, and preserves security defaults (masked secrets, fail-closed validation, default-deny assumptions). Session actors and persistence remain transport-agnostic and are not coupled to onboarding-specific provider branch logic.

## Goals / Non-Goals

**Goals:**
- Define explicit Termina decision-tree behavior for provider selection and auth-method branching during `netclaw init`.
- Define deterministic OAuth device flow states and transitions, including timeout, deny, and retry/cancel outcomes.
- Define model discovery fallback paths when provider catalog lookups fail or return incomplete data.
- Define follow-up doctor checks that verify onboarding outcomes and provide remediation-first guidance.
- Preserve existing secure input handling and keep OpenRouter as default where operator accepts defaults.

**Non-Goals:**
- Implement new provider SDK integrations beyond existing provider profile model.
- Add browser-based OAuth authorization code flow in MVP.
- Add runtime model auto-routing policies beyond existing primary/fallback semantics.
- Change actor persistence schema for sessions, tools, or Slack message handling.

## Decisions

### Decision: Represent onboarding as explicit decision trees in Termina state machine

The onboarding workflow will formalize provider setup as branching states rather than linear prompts.

Rationale:
- Makes reentrant behavior deterministic and auditable.
- Allows provider- and auth-specific validation without ambiguous transitions.

Alternatives considered:
- Keep linear prompts with conditional skips. Rejected because hidden branch behavior is hard to debug and document.
- Move provider onboarding to plain CLI mode. Rejected because PRD-004 anchors `netclaw init` as Termina TUI.

### Decision: OAuth-first providers use device flow only in MVP

For providers with OAuth support, onboarding will use device authorization flow with explicit states: `StartDeviceAuth`, `ShowUserCode`, `PollToken`, `TokenGranted`, `AuthDenied`, `AuthExpired`, `OperatorCancelled`.

Rationale:
- Works in local terminal environments without callback listeners.
- Keeps secrets out of command-line history and avoids redirect URI complexity.

Alternatives considered:
- Authorization code + local callback server. Rejected for MVP complexity and host-network edge cases.
- Manual token paste. Rejected due to high operator error rate and poor UX.

### Decision: Model discovery uses tiered fallback path with explicit provenance

Model selection will attempt, in order: (1) live provider catalog API, (2) curated provider defaults in config templates, (3) operator manual model entry with validation.

Rationale:
- Preserves onboarding momentum during transient provider outages.
- Maintains operator awareness of confidence level for selected model source.

Alternatives considered:
- Fail onboarding if live catalog unavailable. Rejected due to poor first-run resilience.
- Always require manual model entry. Rejected because it increases setup friction and support load.

### Decision: Doctor includes onboarding follow-up checks tied to decision-tree outputs

`netclaw doctor` will add checks that validate the resolved provider profile, effective auth method, token/API-key availability, model provenance, and fallback readiness.

Rationale:
- Connects first-run onboarding outcomes with post-onboarding troubleshooting.
- Reduces ambiguity when onboarding succeeds with degraded model discovery.

Alternatives considered:
- Keep doctor generic and rely on `config validate`. Rejected because provider auth/model issues need richer runtime-oriented remediation.

## Risks / Trade-offs

- [OAuth polling variability across providers] -> Mitigation: normalize polling interval/backoff policy in provider profile metadata and surface wait state in Termina progress components.
- [Catalog fallback picks outdated models] -> Mitigation: annotate model provenance in config and doctor output, warn when using default/manual sources.
- [Increased onboarding complexity] -> Mitigation: render branch context headers in Termina ("Provider: X | Auth: OAuth device flow") and allow back-navigation without data loss.
- [Security regression from mixed credential modes] -> Mitigation: enforce masked input, redact logs, and fail closed when required auth artifacts are missing for selected branch.

## Migration Plan

1. Add spec deltas for onboarding, provider, and CLI capabilities.
2. Implement provider onboarding state machine branch metadata and transition guards.
3. Implement OAuth device flow handlers and persistence of resulting auth artifacts in existing secure config stores.
4. Implement model discovery fallback sequence and provenance tagging.
5. Extend doctor checks with provider onboarding follow-up diagnostics and remediation text.
6. Rollout behind existing onboarding path without schema-breaking config changes.

Rollback strategy:
- Revert to pre-change onboarding branch behavior while retaining backward-compatible config fields.
- Keep existing API-key path operational if OAuth-specific branches are disabled.

## Open Questions (Resolved)

### Q: Which providers are OAuth-capable vs API-key-only?

**Resolved.** Provider capability matrix for MVP:

| Provider    | Auth Methods               | Model Discovery API         | Notes                          |
|-------------|----------------------------|-----------------------------|--------------------------------|
| Anthropic   | OAuth device flow, API key | `GET /v1/models`            | OAuth-first, API key fallback  |
| OpenAI      | OAuth device flow, API key | `GET /v1/models`            | OAuth-first, API key fallback  |
| OpenRouter   | API key only               | `GET /api/v1/models`        | No OAuth support               |
| Ollama      | None (local)               | `GET /api/tags`             | No auth required               |

Wizard presents OAuth as the recommended path for Anthropic and OpenAI. API key
is always available as fallback. OpenRouter and Ollama skip the auth-method
selection step entirely.

### Q: Should doctor hard-fail or warn for fallback-derived model provenance?

**Resolved.** Warning (exit 2). The provider is reachable and functional — the
model selection just used a degraded source. Doctor output will include the
provenance and a suggestion to re-run model discovery when the catalog is
available again.

### Q: Should onboarding cache model catalogs?

**Resolved.** No. Model selection is infrequent (onboarding and occasional
`netclaw model` invocation). The fallback to curated defaults already covers
the "provider is down" case. A stale cache would create more confusion than
a slightly slower live lookup.

---

## Addendum: Concrete Type Definitions and Multi-Provider Design

This section provides the concrete C# types, state model, and algorithms that
RALPH needs to implement Milestone 1 without ambiguity.

### Provider Capability Model

Extend existing `ProviderEntry` with auth method metadata:

```csharp
// New enum — auth methods a provider supports
public enum AuthMethod
{
    None,        // Ollama — no auth required
    ApiKey,      // Static API key in secrets.json
    OAuthDevice  // OAuth 2.0 device authorization grant
}

// New enum — how a model ID was resolved
public enum ModelDiscoverySource
{
    Live,     // From provider's model listing API
    Defaults, // From curated defaults in config templates
    Manual    // Operator typed it in
}
```

Extend `ProviderEntry` (existing type in `Netclaw.Configuration`):

```csharp
public sealed class ProviderEntry
{
    public string Type { get; set; } = "ollama";
    public string Endpoint { get; set; } = "http://localhost:11434";

    // NEW: resolved auth method for this provider instance
    public AuthMethod AuthMethod { get; set; } = AuthMethod.None;

    // Secret fields use SensitiveString — ToString() returns "***REDACTED***"
    // to prevent accidental logging. TypeConverter enables config binding.
    public SensitiveString? ApiKey { get; set; }
    public SensitiveString? OAuthAccessToken { get; set; }
    public SensitiveString? OAuthRefreshToken { get; set; }
    public DateTimeOffset? OAuthTokenExpiry { get; set; }
}
```

Extend `ModelReference` (existing type in `Netclaw.Configuration`):

```csharp
public sealed class ModelReference
{
    public string Provider { get; set; } = "local-ollama";
    public string ModelId { get; set; } = "qwen3:30b";
    public int? ContextWindow { get; set; }

    // NEW: how this model ID was resolved during onboarding
    public ModelDiscoverySource? Provenance { get; set; }
}
```

**Important:** OAuth tokens go in `secrets.json`, NOT `netclaw.json`. The
`ProviderEntry` class binds from layered config (netclaw.json + secrets.json),
so the secrets overlay provides the token fields while netclaw.json has the
non-secret fields (Type, Endpoint, AuthMethod).

### Provider Capability Registry

Static metadata about what each provider type supports. Not persisted — compiled
into the application:

```csharp
public static class ProviderCapabilities
{
    public static IReadOnlyList<AuthMethod> GetSupportedAuthMethods(string providerType)
        => providerType.ToLowerInvariant() switch
        {
            "anthropic" => [AuthMethod.OAuthDevice, AuthMethod.ApiKey],
            "openai" => [AuthMethod.OAuthDevice, AuthMethod.ApiKey],
            "openrouter" => [AuthMethod.ApiKey],
            "ollama" => [AuthMethod.None],
            _ => [AuthMethod.ApiKey] // unknown providers default to API key
        };

    public static bool SupportsModelDiscovery(string providerType)
        => providerType.ToLowerInvariant() is
            "ollama" or "openrouter" or "anthropic" or "openai";
}
```

### Wizard State Machine

The init wizard has 6 steps. Step 1 (LLM provider) branches based on provider
type and auth method:

```
Step 1: Provider Selection
├── SelectionListNode: [Anthropic, OpenAI, OpenRouter, Ollama]
│
├── IF provider supports multiple auth methods:
│   └── Step 1a: Auth Method Selection
│       ├── SelectionListNode: [OAuth Device Flow (recommended), API Key]
│       │
│       ├── IF OAuth Device Flow:
│       │   └── Step 1b: OAuth Device Flow
│       │       ├── TextNode: "Visit {verification_uri}"
│       │       ├── TextNode: "Enter code: {user_code}"
│       │       ├── SpinnerNode: "Waiting for authorization..."
│       │       └── Outcomes: success → Step 1c | denied/expired → retry or back
│       │
│       └── IF API Key:
│           └── Step 1b: API Key Entry
│               └── TextInputNode (masked): "Enter API key"
│
├── IF provider is API-key-only (OpenRouter):
│   └── Step 1b: API Key Entry
│       └── TextInputNode (masked): "Enter API key"
│
├── IF provider is Ollama:
│   └── Step 1b: Endpoint Configuration
│       └── TextInputNode: "Ollama endpoint" (default: http://localhost:11434)
│
└── Step 1c: Model Selection
    ├── Attempt live discovery → defaults → manual
    ├── SelectionListNode: [discovered models] or TextInputNode for manual
    └── TextNode: "Source: {provenance}" (live/cache/defaults/manual)

Step 2: Slack Configuration (unchanged from wireframe)
Step 3: ACL Bootstrap (unchanged)
Step 4: MCP Servers (unchanged)
Step 5: Exposure Mode (unchanged)
Step 6: Health Check (unchanged)
```

### Back-Navigation Clearing Rules

When the operator presses Esc to go back, downstream state must be cleared if
the changed step invalidates it:

| Changed Step         | Clears                                              |
|----------------------|-----------------------------------------------------|
| Provider selection   | Auth method, auth artifacts, model selection, model provenance |
| Auth method          | Auth artifacts (tokens/API key), model selection (if provider changed) |
| Model selection      | Nothing downstream                                  |
| Slack config         | Nothing downstream                                  |
| ACL bootstrap        | Nothing downstream                                  |
| MCP servers          | Nothing downstream                                  |
| Exposure mode        | Nothing downstream                                  |

Rule: provider change clears everything downstream within Step 1. Auth method
change clears auth artifacts. Steps 2–5 are independent and never clear each
other.

### Multi-Provider Configuration and Dual-Mode CLI

The wizard configures the **first** provider. Post-wizard, operators manage
providers and models through dual-mode commands that follow a consistent
pattern: **no args = Termina TUI discovery, args = single-shot scriptable.**

#### `netclaw provider` — Provider credential management

```
netclaw provider              # TUI: guided walk-through (add provider, auth,
                              # model selection — reuses wizard Step 1 components)
netclaw provider add          # single-shot: add with explicit args
  --name my-anthropic
  --type anthropic
  --auth-method oauth-device
netclaw provider list         # plain CLI: show configured providers + auth status
netclaw provider remove <name> # plain CLI: remove provider (warn if models reference it)
```

Bare `netclaw provider` launches the same Termina components used in the wizard's
Step 1 — provider selection, auth method branching, OAuth device flow, credential
entry. This is the "hold my hand" path and also serves as the entry point for
OAuth flows that require interactive browser authorization.

#### `netclaw model` — Model selection and role assignment

```
netclaw model                 # TUI: tree-based browser showing all providers
                              # and their available models, current role assignments
netclaw model                 # single-shot: assign model to role directly
  --role main
  --provider my-anthropic
  --model claude-sonnet-4-20250514
```

Bare `netclaw model` launches Termina with a tree-based model browser:

```
╭─ Model Selection ────────────────────────────────────────────╮
│                                                              │
│  Current assignments:                                        │
│    Main:       claude-sonnet-4-20250514 (my-anthropic)       │
│    Fallback:   qwen3:30b (local-ollama)                      │
│    Compaction: qwen3:8b (local-ollama)                       │
│                                                              │
│  Select role to change: [Main ▾]                             │
│                                                              │
│  Available models:                                           │
│  ├── my-anthropic (OAuth ✓)                                  │
│  │   ├── claude-sonnet-4-20250514 (128k) ← current          │
│  │   ├── claude-haiku-4-5-20251001 (200k)                    │
│  │   └── claude-opus-4-20250514 (200k)                       │
│  ├── local-ollama                                            │
│  │   ├── qwen3:30b (32k)                                    │
│  │   └── qwen3:8b (32k)                                     │
│  └── my-openrouter (API key ✓)                               │
│      ├── google/gemini-2.5-pro                               │
│      └── anthropic/claude-sonnet-4-20250514                  │
│                                                              │
│  [Enter] Select   [Esc] Cancel   [Ctrl+Q] Quit              │
╰──────────────────────────────────────────────────────────────╯
```

The tree is populated from model discovery (live → curated defaults) across all
configured providers. The model selector component is shared with the wizard's
Step 1c.

Config structure in `netclaw.json`:

```json
{
  "Providers": {
    "my-anthropic": {
      "Type": "anthropic",
      "Endpoint": "https://api.anthropic.com",
      "AuthMethod": "OAuthDevice"
    },
    "my-openrouter": {
      "Type": "openrouter",
      "Endpoint": "https://openrouter.ai/api",
      "AuthMethod": "ApiKey"
    },
    "local-ollama": {
      "Type": "ollama",
      "Endpoint": "http://localhost:11434",
      "AuthMethod": "None"
    }
  },
  "Models": {
    "Main": { "Provider": "my-anthropic", "ModelId": "claude-sonnet-4-20250514" },
    "Fallback": { "Provider": "local-ollama", "ModelId": "qwen3:30b" },
    "Compaction": { "Provider": "local-ollama", "ModelId": "qwen3:8b" }
  }
}
```

Secrets in `secrets.json`:

```json
{
  "Providers": {
    "my-anthropic": {
      "OAuthAccessToken": "sk-ant-...",
      "OAuthRefreshToken": "rt-ant-...",
      "OAuthTokenExpiry": "2026-03-25T00:00:00Z"
    },
    "my-openrouter": {
      "ApiKey": "sk-or-..."
    }
  }
}
```

### ChatClientFactory Extension Points

The existing `ChatClientFactory` switch expression adds new cases:

```csharp
return provider.Type.ToLowerInvariant() switch
{
    "ollama" => new OllamaApiClient(...),
    "openrouter" => CreateOpenRouterClient(provider, model),
    "anthropic" => CreateAnthropicClient(provider, model),
    "openai" => CreateOpenAIClient(provider, model),
    _ => throw new InvalidOperationException(...)
};
```

Each `Create*Client` method reads from the layered config (netclaw.json +
secrets.json) to get both endpoint/type and auth credentials.

### Headless TUI Testing

Termina 0.6.0 ships `VirtualTerminal` and `VirtualInputSource` for headless
testing. The wizard tests should:

1. Create `VirtualTerminal` + `VirtualInputSource`
2. Launch `InitWizardPage` headlessly
3. Simulate keystrokes via `VirtualInputSource` to navigate wizard steps
4. Assert on `VirtualTerminal` output state

This allows RALPH to write comprehensive wizard tests without needing a real
terminal or live provider credentials. Use fake/mock provider backends for
credential validation steps.

### RALPH Phase Split

Milestone 1 is split into two phases to separate autonomous work from
credential-dependent interactive work:

**Phase A (autonomous — RALPH can execute alone):**
- Config types: `AuthMethod`, `ModelDiscoverySource`, `ProviderEntry` extensions
- `ProviderCapabilities` static registry
- `ChatClientFactory` provider type cases (OpenRouter, Anthropic, OpenAI)
- Init wizard scaffold with Termina (page, view model, step state machine)
- Back-navigation clearing algorithm
- Fake/mock provider backends for testing
- Headless TUI tests via `VirtualTerminal`
- Doctor config-shape checks (does provider entry have required fields?)

**Phase B (interactive — needs operator for real credentials):**
- Real OAuth device flow endpoints (Anthropic, OpenAI)
- Real API key validation against live endpoints
- Real model catalog discovery from live APIs
- `netclaw provider` TUI and `provider add/list/remove` single-shot commands
- `netclaw model` TUI and single-shot model role assignment
- End-to-end onboarding smoke test with real provider

---

## Implementation Notes (from Phase A build-out)

Findings from implementing the init wizard provider validation and model
discovery (Feb 2026). These affect Phase B planning.

### Anthropic OAuth Device Flow — confirmed viable

In Feb 2026, Anthropic initially appeared to ban all third-party OAuth use but
quickly clarified it was a "docs clean up" that caused confusion. The actual
policy:

- **OAuth via Agent SDK for local/personal use: allowed.** This is Netclaw's
  use case (homelab assistant).
- **Commercial businesses built on OAuth tokens: should use API keys instead.**
- Quote from Anthropic: "Nothing is changing about how you can use the Agent SDK
  and MAX subscriptions."

Sources:
- https://thenewstack.io/anthropic-agent-sdk-confusion/
- https://alternativeto.net/news/2026/2/anthropic-officially-bans-using-subscription-authentication-for-third-party-claude-use/

The Anthropic SDK handles token lifecycle (refresh, expiry). Phase B
implementation needs RFC 8628 device authorization grant polling loop +
storing/refreshing tokens via the SDK.

### OpenRouter model listing is public

OpenRouter's `GET /api/v1/models` returns 200 with the full model catalog
regardless of whether the bearer token is valid. The API key is only validated
on actual inference calls. This means:

- **Probe validates connectivity but NOT key validity** for OpenRouter.
- Users with a bogus key will successfully complete onboarding and only discover
  the key is bad when they try to chat.
- A future `netclaw doctor` check could hit a key-validation endpoint (e.g.,
  `/api/v1/auth/key`) to catch this, but that's separate from onboarding.

### Termina DynamicLayoutNode factory purity rule

DynamicLayoutNode factories must be **pure render functions** — no state
mutations, no `Invalidate()` calls, no sub-step transitions. Calling
`SetProviderSubStep()` inside a factory re-entrantly invalidates the node during
its own evaluation, blanking the screen.

State transitions belong in reactive subscriptions outside the factory. Filed
as Termina issue: https://github.com/Aaronontheweb/termina/issues/159
