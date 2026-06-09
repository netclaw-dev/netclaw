// mock/store.js
//
// Shared in-memory config state for the `netclaw config` tracer bullet. The
// dashboard reads summaries from here; the editors mutate it on autosave. That
// closes the loop the real product cares about: a completed action persists, and
// re-entering a screen (or the dashboard) reflects the new state — proving the
// autosave + reentrancy contract without a real backend.

export const FEATURES = ['Memory', 'Search', 'Skills', 'Scheduling', 'SubAgents', 'Webhooks'];

export const FEATURE_DESC = {
  Memory: 'Remember facts across sessions.',
  Search: 'Web search tools (provider-gated).',
  Skills: 'Load and sync skill files.',
  Scheduling: 'Cron and reminder tools.',
  SubAgents: 'Spawn delegated sub-agents.',
  Webhooks: 'Outbound webhook delivery.',
};

export const store = {
  providersConfigured: 2,
  mainModel: 'claude-sonnet-4-20250514',
  channels: {
    Slack: {
      configured: true,
      channels: [{ id: 'C01ABC', name: '#general', audience: 'Team' }, { id: 'C02XYZ', name: '#ops', audience: 'Personal' }],
      users: 'U12345',
      dms: { enabled: false, audience: 'Personal' },
    },
    Discord: { configured: false },
    Mattermost: { configured: false },
  },
  inbound: { enabled: false, timeoutSeconds: 45 },
  searchBackend: 'brave',          // duckduckgo | brave | searxng | none
  browser: { enabled: false, backend: 'Playwright · Chromium' },
  telemetry: {
    enabled: false, otlp: 'http://localhost:4317',
    // NotificationsConfig.Webhooks (List<WebhookTarget>): { Url, Name, Headers, Format }.
    // We model one header (an Authorization-style entry) and auto-detect Format from the URL.
    webhooks: [
      { id: 1, name: 'pagerduty', url: 'https://events.pagerduty.com/v2/enqueue', header: 'Authorization: Token abc123', enabled: true },
    ],
    nextWebhookId: 2,
  },
  posture: 'Team',                 // Personal | Team | Public
  features: { Memory: true, Search: true, Skills: true, Scheduling: true, SubAgents: false, Webhooks: false },
  workspacesDir: '~/projects',
  exposureMode: 'Local',           // Local | Reverse Proxy | Tailscale Serve | Tailscale Funnel | Cloudflare Tunnel
  rpHost: '',                      // preserved even when another mode is active (inactive-value retention)
  rpProxies: '',
};

export const enabledCount = () => FEATURES.filter((f) => store.features[f]).length;

// ── Audience Profiles (Security & Access -> Audience Profiles) ──
export const AUDIENCES = ['Personal', 'Team', 'Public'];
export const AUDIENCE_DESC = {
  Personal: 'Operator/local sessions.',
  Team: 'Trusted internal channels.',
  Public: 'Untrusted external users.',
};
// File-scope options are wider for Personal; restricted audiences can't pick "All files".
export const FILE_SCOPES = { Personal: ['Off', 'Session only', 'All files'], Team: ['Off', 'Session only'], Public: ['Off', 'Session only'] };
export const ATTACHMENT_LEVELS = ['None', 'Images', 'Common work files', 'All attachments'];

const audienceDefaults = () => ({
  Personal: { fileTools: true, web: true, skills: true, scheduling: true, changeWorkspace: true, fileScope: 'All files', attachments: 'All attachments', customized: false },
  Team: { fileTools: true, web: true, skills: true, scheduling: true, changeWorkspace: true, fileScope: 'Session only', attachments: 'Common work files', customized: false },
  Public: { fileTools: false, web: false, skills: false, scheduling: false, changeWorkspace: false, fileScope: 'Off', attachments: 'None', customized: false },
});
store.audienceProfiles = audienceDefaults();
export const resetAudience = (aud) => { store.audienceProfiles[aud] = audienceDefaults()[aud]; };

// ── Skill Sources (local folders + remote skill servers) ──
store.skills = {
  sources: [
    { id: 1, kind: 'local', name: 'team-skills', location: '~/skills/team', enabled: true, symlinks: false, skillCount: 12, status: '12 skills' },
    { id: 2, kind: 'local', name: 'personal', location: '~/.netclaw/skills', enabled: true, symlinks: false, skillCount: 8, status: '8 skills' },
    { id: 3, kind: 'remote', name: 'acme-feed', location: 'https://skills.acme.io', enabled: true, auth: 'bearer', hasToken: true, syncInterval: '1h', skillCount: 27, status: '27 skills · synced 2h ago' },
  ],
  nextId: 4,
};
export const skillTotals = () => {
  const ss = store.skills.sources;
  return { skills: ss.reduce((a, s) => a + (s.enabled ? s.skillCount : 0), 0), dirs: ss.filter((s) => s.kind === 'local').length, feeds: ss.filter((s) => s.kind === 'remote').length };
};

export const SEARCH_LABEL = { duckduckgo: 'DuckDuckGo', brave: 'Brave', searxng: 'SearXNG', none: 'Not set' };
export const searchLabel = () => SEARCH_LABEL[store.searchBackend] || store.searchBackend;

export const BROWSER_BACKENDS = ['Playwright · Chromium', 'Playwright · Firefox', 'System Chrome/Chromium'];
