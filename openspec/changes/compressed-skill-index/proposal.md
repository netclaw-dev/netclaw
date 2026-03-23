## Why

The current skill index injected into the system prompt is verbose (~2000+
tokens for 7 skills) with full descriptions and absolute file paths. Research
from dotnet-skills evals shows that a compressed index (688 tokens) achieves
56.5% TPR vs 21.7% for a fat index — information overload suppresses skill
activation. Additionally, the index is identical for all sessions regardless
of trust audience, meaning Public sessions see skills they can't meaningfully
use (e.g., diagnostics requiring shell access).

Ref: PRD-001 FR-006 (Layered System Prompt), PRD-002 (Gateway Security).

## What Changes

- Replace `SkillRegistry.GenerateDescriptionMenu()` with a compressed
  pipe-delimited format grouped by category, referencing `skill_load` instead
  of `file_read`
- Add an `IHostedService` that uses an LLM sidecar to generate short trigger
  phrases per skill (cached to disk by name+version), bridging operator
  language to user language for the compressed index
- Add audience-aware skill filtering: skills whose `allowed-tools` are not
  available at the session's trust level are excluded from the index
- Add trust-tier-based visibility filtering: Community skills visible only to
  Team+Personal, External/Agent skills visible only to Personal
- Make `SkillIndexContextLayer` audience-aware so each session gets a
  tailored, compact index

## Capabilities

### New Capabilities

- `skill-index-compression`: Compressed pipe-delimited skill index format
  with LLM-generated trigger phrases, audience-aware filtering, and
  trust-tier visibility rules

### Modified Capabilities

- `netclaw-session`: Session prompt assembly must pass effective audience and
  available tools to the skill index context layer for per-session filtering

## Impact

- `src/Netclaw.Actors/Skills/SkillRegistry.cs` — rewrite
  `GenerateDescriptionMenu()`, add filtering overload
- `src/Netclaw.Configuration/SkillIndexContextLayer.cs` — audience-aware menu
  selection
- `src/Netclaw.Daemon/Services/SkillIndexEnrichmentService.cs` — new hosted
  service for sidecar trigger phrase generation
- `src/Netclaw.Daemon/Program.cs` — register enrichment service
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` — pass audience context to
  skill index layer
- Depends on `SkillTrustTier` enum (from sibling change trust-tiers-security-stub)
- Depends on `ToolAccessPolicy` for available tool sets per audience
