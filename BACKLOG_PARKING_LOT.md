# Backlog Parking Lot

This file holds non-NOW work so autonomous loops do not accidentally bulldoze
deprioritized tasks. Move items into `IMPLEMENTATION_PLAN.md` only when the user
explicitly changes priority.

## NEXT Candidates

- Webhook service identity and inbound webhook hardening.
- Subagent explicit model selection.
- Subagent parent-context alignment.
- GitHub Copilot provider refinements.
- VLLM capability strategy and timing work.
- Fixed-length approval button labels and richer approval UI.
- Config hot-reload beyond startup-time configuration.
- Operator diagnostics refinements beyond current CLI/doctor/status work.

## LATER Candidates

- Ambient channel monitoring workflows.
- Delegated coding task orchestration.
- Browser automation as a first-class product feature.
- Split gateway/agent process architecture.
- Hosted/multi-tenant operator console.
- Delivery-policy tuning beyond the first Telemetry & Alerting config pass.

## Parking Rule

If a future task is interesting but not necessary for the active milestone, add
it here instead of expanding `NOW`. The implementation plan should stay small
enough that an agent can finish the selected task all the way through runtime
verification.
