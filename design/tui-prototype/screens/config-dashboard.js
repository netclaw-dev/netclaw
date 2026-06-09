// screens/config-dashboard.js
//
// `netclaw config` root. Faithful to ConfigDashboardViewModel's item list/order,
// but renders a scannable STATUS-SUMMARY column (the TUI-002 redesign) instead of
// the current static-description column, with the focused item's description shown
// as a dim help line. Summaries are read live from the mock store, so edits made
// in the sub-editors are reflected here on return (reentrancy + autosave).

import { store, enabledCount, searchLabel, skillTotals } from '../mock/store.js';

const onOff = (b) => (b ? 'enabled' : '– disabled');

const tele = () => { const n = store.telemetry.webhooks.length; return `OTLP ${store.telemetry.enabled ? 'on' : 'off'} · ${n} webhook${n === 1 ? '' : 's'}`; };

// label, summary(), description, route. Order matches the real dashboard.
// route: a registered config screen id, 'handoff:<cmd>' for a routed command, or
// null for a terminal action (Doctor / Quit).
const ITEMS = [
  ['Inference Providers', () => `${store.providersConfigured} configured`, 'Manage provider definitions and authentication.', 'handoff:provider'],
  ['Models', () => store.mainModel, 'Assign model roles and discover provider models.', 'handoff:model'],
  ['Channels', () => { const cfg = ['Slack', 'Discord', 'Mattermost'].filter((a) => store.channels[a].configured); if (cfg.length === 0) return '– none configured'; if (cfg.length === 1) return `${cfg[0]} · ${store.channels[cfg[0]].channels.length} channels`; return cfg.join(' · '); }, 'Slack, Discord, and Mattermost settings.', 'config-channels'],
  ['Inbound Webhooks', () => onOff(store.inbound.enabled), 'Global webhook enablement and route diagnostics.', 'config-inbound'],
  ['Skill Sources', () => { const t = skillTotals(); return `${t.skills} skills · ${t.dirs} dirs · ${t.feeds} feeds`; }, 'External skills and private skill feeds.', 'config-skills'],
  ['Search', () => (store.searchBackend === 'none' ? '– not set' : `✓ ${searchLabel()}`), 'Search backend and credentials.', 'config-search'],
  ['Browser Automation', () => onOff(store.browser.enabled), 'Canonical browser MCP profile settings.', 'config-browser'],
  ['Telemetry & Alerting', tele, 'Telemetry and outbound webhook alerting.', 'config-telemetry'],
  ['Security & Access', () => `${store.posture} · ${enabledCount()}/6 enabled`, 'Posture, enabled features, audience profiles, and exposure mode.', 'config-security'],
  ['Workspaces Directory', () => store.workspacesDir, 'Project discovery root for workspace-aware prompts.', 'config-workspaces'],
  ['Run Full Doctor', () => '', 'Exit the dashboard and run `netclaw doctor`.', null],
  ['Quit', () => '', 'Exit without changing settings.', null],
];

const CONFIG_SCREENS = new Set([
  'config-search', 'config-security', 'config-channels', 'config-skills', 'config-inbound', 'config-browser', 'config-telemetry', 'config-workspaces',
]);

const LABEL_W = 22;

export const configDashboard = {
  id: 'config-dashboard',
  state: { index: 0 },

  init() { this.state.index = 0; },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Netclaw Config');
    W.heading(scr, r, r.y, 'Settings Areas');

    const rows = ITEMS.map(([label, summary]) => {
      const s = summary();
      return s ? `${label.padEnd(LABEL_W)}  ${s}` : label;
    });
    const after = W.selectionList(scr, r, r.y + 1, rows, this.state.index);

    // Focused item's description as a dim help line.
    W.helpLines(scr, r, after + 1, [ITEMS[this.state.index][2]]);

    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Quit  [Ctrl+Q] Quit');
  },

  onKey(k, rt) {
    const s = this.state;
    if (k === 'up') { s.index = Math.max(0, s.index - 1); rt.setStatus(null); }
    else if (k === 'down') { s.index = Math.min(ITEMS.length - 1, s.index + 1); rt.setStatus(null); }
    else if (k === 'enter') {
      const [label, , , route] = ITEMS[s.index];
      if (CONFIG_SCREENS.has(route)) rt.go(route);
      else if (route && route.startsWith('handoff:')) rt.setStatus(`${label} is a routed handoff to \`netclaw ${route.split(':')[1]}\` — a separate command surface.`, 'dim');
      else if (label === 'Run Full Doctor') rt.setStatus('(prototype) would exit and run `netclaw doctor`.', 'dim');
      else if (label === 'Quit') rt.setStatus('(prototype) would exit `netclaw config`.', 'dim');
      else rt.setStatus(`${label} is not yet built in this prototype.`, 'warn');
    }
  },
};
