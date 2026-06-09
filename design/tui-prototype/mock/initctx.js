// mock/initctx.js
//
// Shared context for the simplified `netclaw init` wizard. Each step writes its
// result here; the Health Check step reads it back for the summary. Mirrors the
// WizardContext the real orchestrator threads through the steps.

export const initCtx = {
  provider: 'Anthropic',
  model: 'claude-sonnet-4-20250514',
  identity: { agentName: 'netclaw', userName: '', timezone: 'America/New_York', workspaces: '~/projects' },
  posture: 'Personal',
  features: { Memory: true, Search: true, Skills: true, Scheduling: true, SubAgents: true, Webhooks: true },
};

// Feature defaults per posture (feature-selection-wizard spec). Personal skips the
// step entirely (everything enabled); Team gets most on, Public starts all off.
export const FEATURE_DEFAULTS = {
  Team: { Memory: true, Search: true, Skills: true, Scheduling: true, SubAgents: true, Webhooks: true },
  Public: { Memory: false, Search: false, Skills: false, Scheduling: false, SubAgents: false, Webhooks: false },
};
