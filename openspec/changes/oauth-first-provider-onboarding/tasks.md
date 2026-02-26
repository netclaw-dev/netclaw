## 1. Onboarding Decision Tree Foundation

- [ ] 1.1 Add provider onboarding branch state model for Termina wizard (provider selection, auth method, model discovery path)
- [ ] 1.2 Implement branch transition guards so back-navigation recalculates and clears invalid downstream values
- [ ] 1.3 Render branch context indicators in Termina (`provider`, `auth method`, `model source`) and verify updates on branch changes

## 2. OAuth Device Flow Branch

- [ ] 2.1 Add OAuth-capable provider profile metadata and auth-method resolver (`oauth-device` vs `api-key`)
- [ ] 2.2 Implement OAuth device flow sequence (`start`, `show code`, `poll`, `success`, `denied/expired/cancel`) with retry and branch-switch actions
- [ ] 2.3 Persist OAuth auth artifacts through existing secret-safe config pipeline and verify redaction in logs/output

## 3. Model Discovery Fallback Paths

- [ ] 3.1 Implement model discovery fallback order (live catalog -> cache -> curated defaults -> manual entry)
- [ ] 3.2 Persist model provenance (`live`, `cache`, `defaults`, `manual`) with provider config
- [ ] 3.3 Add onboarding completion summary details for selected model source and fallback/degraded state

## 4. Doctor Follow-Up Checks

- [ ] 4.1 Extend `netclaw doctor` checks for provider onboarding outcomes (auth method, auth artifact presence, primary/fallback validity, model provenance)
- [ ] 4.2 Add remediation-first output text for each failure/degraded check and wire exit code behavior (0/1/2)
- [ ] 4.3 Ensure degraded fallback-only model state returns warning exit code 2 unless required auth/provider checks fail

## 5. Validation, Traceability, and Spec Hygiene

- [ ] 5.1 Add/adjust tests for onboarding branch transitions, OAuth device flow failure recovery, and model fallback sequencing
- [ ] 5.2 Add/adjust tests for doctor follow-up checks and exit-code semantics
- [ ] 5.3 Update CLI/operator docs to reflect OAuth-first onboarding tree and doctor follow-up checks, with explicit references to PRD-004 and PRD-005
