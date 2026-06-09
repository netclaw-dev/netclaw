// screens/config-rows.js
//
// A shared row-based leaf editor for the UNIFORM config areas (Inbound Webhooks,
// Browser Automation, Telemetry). This is the deliberate counterpoint to the
// "universal framework" wart: genuinely-uniform leaves share one small component,
// while variant editors (Search, Exposure, Channels, Provider) stay bespoke. Each
// row is a label + an inline value whose kind drives interaction:
//   toggle  Space/Enter flips a bool          cycle  ←/→ steps an option list
//   text    type to edit a draft, Enter saves handoff Space/Enter notes a routed cmd
// Every mutation autosaves to the store and shows a "Saved." status.

import { store, BROWSER_BACKENDS } from '../mock/store.js';

const LABEL_W = 24;

function makeRowEditor({ id, title, intro, rows, footer, keys }) {
  return {
    id, title, rows,
    state: {},
    init() {
      this.state = { rowIndex: 0, drafts: {} };
      rows.forEach((r) => { if (r.kind === 'text') this.state.drafts[r.key] = r.get() || ''; });
    },
    isAnimating() { return rows[this.state.rowIndex]?.kind === 'text'; },

    rowValue(row, focused) {
      if (row.kind === 'toggle') return `[${row.get() ? 'x' : ' '}]`;
      if (row.kind === 'cycle') return `[◀ ${row.get().padEnd(22)} ▶]`;
      if (row.kind === 'handoff') return row.value;
      if (row.kind === 'route') return row.value();
      if (row.kind === 'text') {
        const d = this.state.drafts[row.key] ?? '';
        const shown = d || row.placeholder || '';
        if (focused && Math.floor(performance.now() / 530) % 2 === 0) return `${shown}█`;
        return shown;
      }
      return row.get();
    },

    render(scr, rt, W) {
      const r = W.pageFrame(scr, title);
      W.heading(scr, r, r.y, title);
      let yy = r.y + 1;
      (intro ? intro() : []).forEach((l) => { W.helpLines(scr, r, yy, [l]); yy += 1; });
      yy += 1; // blank before rows

      rows.forEach((row, i) => {
        const focused = i === this.state.rowIndex;
        const line = `${row.label.padEnd(LABEL_W)} ${this.rowValue(row, focused)}`;
        if (focused) {
          scr.fillRect(r.x, yy, r.w, 1, ' ', { bg: 'accent', fg: 'onAccent' });
          scr.text(r.x, yy, line, { bg: 'accent', fg: 'onAccent' });
        } else {
          scr.text(r.x, yy, line, { fg: 'text' });
        }
        yy += 1;
      });

      yy += 1;
      (footer ? footer() : []).forEach((f) => { scr.text(r.x + 2, yy, f.text, { fg: f.color || 'dim' }); yy += 1; });
      W.helpLines(scr, r, yy + 1, [rows[this.state.rowIndex].desc]);

      if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
      W.keyHints(scr, r, keys);
    },

    onKey(k, rt) {
      const s = this.state;
      const row = rows[s.rowIndex];
      if (k === 'up') { s.rowIndex = Math.max(0, s.rowIndex - 1); rt.setStatus(null); return; }
      if (k === 'down') { s.rowIndex = Math.min(rows.length - 1, s.rowIndex + 1); rt.setStatus(null); return; }
      if (k === 'escape') { rt.back(); return; }

      if (row.kind === 'toggle') {
        if (k === 'space' || k === 'enter') rt.setStatus(row.toggle(), 'ok');
      } else if (row.kind === 'cycle') {
        if (k === 'left') rt.setStatus(row.step(-1), 'ok');
        else if (k === 'right' || k === 'space' || k === 'enter') rt.setStatus(row.step(1), 'ok');
      } else if (row.kind === 'handoff') {
        if (k === 'space' || k === 'enter') rt.setStatus(row.activate(), 'dim');
      } else if (row.kind === 'route') {
        if (k === 'space' || k === 'enter') rt.go(row.route);
      } else if (row.kind === 'text') {
        if (k === 'enter') rt.setStatus(row.save(s.drafts[row.key]), 'ok');
        else if (k === 'backspace') s.drafts[row.key] = (s.drafts[row.key] || '').slice(0, -1);
        else if (k === 'space') s.drafts[row.key] = (s.drafts[row.key] || '') + ' ';
        else if (k.length === 1) s.drafts[row.key] = (s.drafts[row.key] || '') + k;
      }
    },
  };
}

// ── Inbound Webhooks ──
export const configInbound = makeRowEditor({
  id: 'config-inbound', title: 'Inbound Webhooks',
  intro: () => ['Global webhook enablement lives here. Route files stay owned by `netclaw webhooks`.'],
  rows: [
    { kind: 'toggle', label: 'Enabled', desc: 'Toggle global webhook endpoint registration.',
      get: () => store.inbound.enabled, toggle: () => { store.inbound.enabled = !store.inbound.enabled; return `Inbound webhooks ${store.inbound.enabled ? 'enabled' : 'disabled'}. Saved.`; } },
    { kind: 'text', key: 'timeout', label: 'Execution timeout (s)', desc: 'Maximum autonomous webhook run time before failure.',
      get: () => String(store.inbound.timeoutSeconds), save: (d) => { store.inbound.timeoutSeconds = parseInt(d, 10) || store.inbound.timeoutSeconds; return `Execution timeout set to ${store.inbound.timeoutSeconds}s. Saved.`; } },
    { kind: 'handoff', label: 'Route authoring', value: 'netclaw webhooks', desc: 'Use `netclaw webhooks set|list|validate`; this editor never creates dummy routes.',
      activate: () => 'Routes are authored with `netclaw webhooks` (separate command).' },
  ],
  footer: () => [
    { text: 'Routes: total=0, enabled=0, disabled=0, invalid=0', color: 'dim' },
    ...(store.inbound.enabled
      ? [{ text: 'Enabled — now add routes with `netclaw webhooks set`. Requests fail closed until at least one route exists.', color: 'warn' }]
      : [{ text: 'Enable the endpoint first, then add routes with `netclaw webhooks set`.', color: 'dim' }]),
  ],
  keys: '[↑/↓] Navigate  [Space] Toggle/Save  [Type] Edit timeout  [Enter] Apply  [Esc] Settings Areas  [Ctrl+Q] Quit',
});

// ── Browser Automation ──
export const configBrowser = makeRowEditor({
  id: 'config-browser', title: 'Browser Automation',
  intro: () => ["Adds or removes Netclaw's canonical browser MCP profile. Tool grants stay in MCP permissions."],
  rows: [
    { kind: 'toggle', label: 'Enabled', desc: 'Create or remove the canonical browser MCP server profile.',
      get: () => store.browser.enabled, toggle: () => { store.browser.enabled = !store.browser.enabled; return store.browser.enabled ? 'Browser Automation saved. Use MCP permissions to grant access.' : 'Browser Automation disabled and canonical profiles removed.'; } },
    { kind: 'cycle', label: 'Backend', desc: 'Browser runtime used by the canonical MCP profile.',
      get: () => store.browser.backend, step: (d) => { const o = BROWSER_BACKENDS; const i = Math.max(0, o.indexOf(store.browser.backend)); store.browser.backend = o[(i + d + o.length) % o.length]; return `Backend set to ${store.browser.backend}. Saved.`; } },
    { kind: 'handoff', label: 'MCP permissions', value: 'open grant editor', desc: 'Grant browser_automation access per audience in `netclaw mcp permissions`.',
      activate: () => 'Opens `netclaw mcp permissions` (routed handoff).' },
  ],
  footer: () => [{ text: `Runtime check: prerequisites ${store.browser.enabled ? 'required — install Playwright runtime if missing' : 'not checked (disabled)'}`, color: store.browser.enabled ? 'warn' : 'dim' }],
  keys: '[↑/↓] Navigate  [Space/Enter] Activate  [←/→] Backend  [Esc] Settings Areas  [Ctrl+Q] Quit',
});

// ── Telemetry & Alerting ──
export const configTelemetry = makeRowEditor({
  id: 'config-telemetry', title: 'Telemetry & Alerting',
  intro: () => [
    'Configure OpenTelemetry export and operational outbound webhooks.',
    'Delivery-policy tuning is intentionally parked for a later pass.',
    '',
    `Current: telemetry=${store.telemetry.enabled ? 'enabled' : 'disabled'}, outbound webhooks=${store.telemetry.webhooks.length}`,
  ],
  rows: [
    { kind: 'toggle', label: 'Telemetry enabled', desc: 'Toggle daemon OTLP logs and metrics export.',
      get: () => store.telemetry.enabled, toggle: () => { store.telemetry.enabled = !store.telemetry.enabled; return `Telemetry ${store.telemetry.enabled ? 'enabled' : 'disabled'}. Saved.`; } },
    { kind: 'text', key: 'otlp', label: 'OTLP endpoint', desc: 'gRPC OTLP collector endpoint, usually port 4317.',
      get: () => store.telemetry.otlp, save: (d) => { store.telemetry.otlp = d; return 'OTLP endpoint saved.'; } },
    { kind: 'route', label: 'Outbound webhooks', value: () => `${store.telemetry.webhooks.length} configured  →`, route: 'config-webhooks',
      desc: 'Add, edit, or remove operational alert targets. Slack URLs use Slack format automatically.' },
  ],
  keys: '[↑/↓] Navigate  [Space] Toggle  [Type] Edit  [Enter] Apply/Open  [Esc] Settings Areas  [Ctrl+Q] Quit',
});

// ── Workspaces Directory (single-field; its own Current/New shape) ──
export const configWorkspaces = {
  id: 'config-workspaces', title: 'Workspaces Directory',
  state: {},
  init() { this.state = { draft: '' }; },
  isAnimating() { return true; },
  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Workspaces Directory');
    W.heading(scr, r, r.y, 'Workspaces Directory');
    W.helpLines(scr, r, r.y + 1, ['Sets the root Netclaw uses for project discovery and workspace-scoped prompts.']);
    W.line(scr, r, r.y + 3, `Current: ${store.workspacesDir}`, 'fg');
    const caret = Math.floor(performance.now() / 530) % 2 === 0 ? '█' : '';
    W.line(scr, r, r.y + 4, `New:     ${this.state.draft || '(leave unchanged)'}${this.state.draft ? caret : ''}`, 'accent');
    W.helpLines(scr, r, r.y + 6, ['Type a local path. The directory is created if it does not exist.']);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[Type] Edit  [Backspace] Delete  [Enter] Apply  [Esc] Settings Areas  [Ctrl+Q] Quit');
  },
  onKey(k, rt) {
    const s = this.state;
    if (k === 'enter') { if (s.draft.trim()) { store.workspacesDir = s.draft.trim(); rt.setStatus(`Workspaces directory set to ${store.workspacesDir}. Saved.`, 'ok'); s.draft = ''; } }
    else if (k === 'escape') rt.back();
    else if (k === 'backspace') s.draft = s.draft.slice(0, -1);
    else if (k.length === 1) s.draft += k;
  },
};
