# TUI-004: Search Config Progressive Disclosure POC

Source PRDs: `PRD-004`

Related docs:

- `docs/ui/TUI-002-netclaw-config-wireframes.md`
- `docs/prd/PRD-004-cli-onboarding-and-config.md`

Status: design POC for replacing the current Search editor layout.

## Why the current screen fails

The current Search screen tries to show three separate concerns at once:

1. information architecture (`Fields` list)
2. editing UI (`Selected Field`)
3. command surface (`Actions`)

That breaks down in a terminal UI for a few reasons:

- the operator has to understand the screen layout before they can do the task
- the `Actions` area reads like static text instead of an obvious next step
- irrelevant fields are visible before the backend choice has narrowed the problem
- the screen looks data-driven instead of goal-driven
- focus movement is ambiguous because there are multiple active-looking regions

The operator's real task is much smaller: choose a provider, fill only the fields
that matter, test it, save it, go back.

## Design goals

1. One decision per screen.
2. Only show fields that matter for the chosen backend.
3. Keep the primary action obvious at every step.
4. Treat testing and save as the end of the flow, not a third parallel panel.
5. Keep quiet states quiet.

## Recommended interaction model

Use a short staged flow inside `/search`.

### Stage 1: Search summary

Purpose: orient the operator and let them decide whether they want to change,
test, or leave Search alone.

Show only:

- current backend
- only backend-specific state that is actually meaningful
- three actions: `Change provider`, `Test current config`, `Back`

Do not show filler copy like `Secret status: Not required` or `Last check: Ready`
for a quiet/default state.

### Stage 2: Choose provider

Purpose: make the only important decision first.

Show a single selection list with one-line descriptions:

- DuckDuckGo
- Brave
- SearXNG

No form fields on this screen.

### Stage 3: Configure selected provider

Purpose: only collect the fields required for the selected backend.

Behavior by backend:

- DuckDuckGo: no extra fields, just confirmation, test, and save
- Brave: API key field only
- SearXNG: endpoint URL field only

Show validation only when relevant.

There is no standalone credential-management screen. Credential input only
appears inline on the provider form for backends that actually use one.

Actions live at the bottom of this form:

- `Test`
- `Save`
- `Change provider`
- `Back`

### Modal: Probe failure warning

If structural validation passes but the runtime probe fails, show a blocking
warning dialog:

- `Keep editing`
- `Test again`
- `Save anyway`

This stays off the main screen until needed.

## Workflow diagram

```text
Dashboard
  |
  v
Search summary
  |
  +--> Back to dashboard
  |
  +--> Test current config
  |       |
  |       +--> success/failure status on summary
  |
  +--> Change provider
          |
          v
      Choose provider
          |
          v
      Provider-specific form
          |
          +--> Back
          |      |
          |      +--> Search summary
          |
          +--> Test
          |      |
          |      +--> success -> inline success state
          |      |
          |      +--> failure -> probe warning dialog
          |
          +--> Save
                 |
                 +--> structural error -> stay on form, show issues
                 |
                 +--> probe success -> persist and return to summary
                 |
                 +--> probe failure -> warning dialog

Persist on save:
  - Search.Backend -> netclaw.json
  - Search.SearXngEndpoint -> netclaw.json
  - Search.BraveApiKey -> secrets.json
```

## Mockups

### Screen A: Search summary

```text
╭─ Search ─────────────────────────────────────────────────────╮
│                                                             │
│  Configure how Netclaw performs web search and URL fetch.   │
│                                                             │
│  Current provider: DuckDuckGo                               │
│  No additional setup required.                              │
│                                                             │
│  ▸ Change provider                                          │
│    Test current configuration                               │
│    Back to dashboard                                        │
│                                                             │
│ ↑/↓ navigate · Enter select · Esc back                      │
╰─────────────────────────────────────────────────────────────╯
```

Why this is better:

- no editing surface until the operator asks to edit
- no dead-looking action panel
- summary is readable in under five seconds

### Screen A2: Search summary with meaningful state

```text
╭─ Search ─────────────────────────────────────────────────────╮
│                                                             │
│  Current provider: Brave                                    │
│  API key configured.                                        │
│                                                             │
│  ▸ Change provider                                          │
│    Test current configuration                               │
│    Back to dashboard                                        │
│                                                             │
│ ↑/↓ navigate · Enter select · Esc back                      │
╰─────────────────────────────────────────────────────────────╯
```

If the current state is not meaningful, do not surface it. If it matters,
surface it in one short line.

### Screen B: Choose provider

```text
╭─ Search › Choose Provider ──────────────────────────────────╮
│                                                             │
│  How should Netclaw search the web?                         │
│                                                             │
│  ▸ DuckDuckGo                                               │
│    No key required. Good default for most installs.         │
│                                                             │
│    Brave                                                    │
│    Faster search results. Requires an API key.              │
│                                                             │
│    SearXNG (self-hosted)                                    │
│    Use your own endpoint URL.                               │
│                                                             │
│ Enter choose · Esc back                                     │
╰─────────────────────────────────────────────────────────────╯
```

Why this is better:

- the provider decision is isolated from credentials and actions
- descriptions answer "why would I pick this?" in place

### Screen C1: Configure Brave

```text
╭─ Search › Brave ────────────────────────────────────────────╮
│                                                             │
│  Provider: Brave                                            │
│                                                             │
│  Brave API key                                              │
│  ╭────────────────────────────────────────────────────────╮ │
│  │                                                        │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  Existing key is configured. Leave blank to keep it.       │
│                                                             │
│  [ Test ]   [ Save ]   [ Change provider ]   [ Back ]      │
│                                                             │
│ Tab next · Enter activate · Esc back                        │
╰─────────────────────────────────────────────────────────────╯
```

If no Brave credential is currently stored, omit the `Existing key is
configured` helper line entirely.

### Screen C2: Configure SearXNG

```text
╭─ Search › SearXNG ──────────────────────────────────────────╮
│                                                             │
│  Provider: SearXNG                                          │
│                                                             │
│  Instance URL                                               │
│  ╭────────────────────────────────────────────────────────╮ │
│  │ https://search.example.com                             │ │
│  ╰────────────────────────────────────────────────────────╯ │
│  Enter the base URL of your SearXNG instance.              │
│                                                             │
│  [ Test ]   [ Save ]   [ Change provider ]   [ Back ]      │
│                                                             │
│ Tab next · Enter activate · Esc back                        │
╰─────────────────────────────────────────────────────────────╯
```

### Screen C3: Configure DuckDuckGo

```text
╭─ Search › DuckDuckGo ───────────────────────────────────────╮
│                                                             │
│  Provider: DuckDuckGo                                       │
│                                                             │
│  No extra settings are required for this provider.          │
│                                                             │
│  [ Test ]   [ Save ]   [ Change provider ]   [ Back ]      │
│                                                             │
│ Tab next · Enter activate · Esc back                        │
╰─────────────────────────────────────────────────────────────╯
```

### Screen D: Probe failure warning

```text
╭─ Search Test Warning ───────────────────────────────────────╮
│                                                             │
│  Netclaw could not complete a live search using this        │
│  configuration.                                             │
│                                                             │
│  Brave returned: HTTP 401 Unauthorized                      │
│                                                             │
│  ▸ Keep editing                                             │
│    Test again                                               │
│    Save anyway                                              │
│                                                             │
│ ↑/↓ navigate · Enter select · Esc keep editing              │
╰─────────────────────────────────────────────────────────────╯
```

## Design principles for this screen

### 1. Decision first, form second

Do not show provider-specific fields until the backend is chosen. The backend
selection is the actual fork in the task.

### 2. Actions belong to the current step

Never keep a persistent side-panel of commands on screen. `Test`, `Save`, and
`Back` should appear only in the context of the current form or summary page.

### 3. State should read like operator language, not schema language

Prefer `Current provider`, `Existing key is configured`, and `No extra settings
required` over exposing raw field architecture like `Fields`, `Selected Field`,
or `Inactive for current backend`.

### 4. No null-state metadata

Do not render rows that only describe the absence of state. If a backend has no
credential concept, do not mention credentials. If there is no meaningful test
history or warning, do not render status copy.

## Conditional rendering rules

- DuckDuckGo summary should not mention credentials.
- DuckDuckGo form should not mention secret status.
- Brave summary may show `API key configured` or `API key required` when that is
  materially useful.
- Brave form should only show `Leave blank to keep it` when a stored secret
  already exists.
- SearXNG should never show secret-management copy.
- `Last check` or similar status copy should only appear after an explicit test
  result or when surfacing a warning/error worth operator attention.

## Implementation notes for the next POC

The next implementation should replace the current `FieldList + FieldCard +
ActionCard` model with a small route-local state machine:

- `Summary`
- `ChooseBackend`
- `ConfigureBackend`
- `ProbeWarning`

That keeps the TUI interactive without making the operator manage focus across
three competing regions.

## VHS validation plan after the redesign lands

Once the new POC exists, validate it with a tight visual loop:

1. add a dedicated Search VHS tape for each backend path
2. capture screenshots for summary, chooser, provider form, and warning dialog
3. run a visual design/usability review pass on those screenshots
4. tighten the layout until the screen is readable without explanation

The key review question should be simple:

"Can a first-time operator understand what to do next within five seconds?"
