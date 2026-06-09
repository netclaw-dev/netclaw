// screens/config-webhooks.js
//
// `netclaw config` -> Telemetry & Alerting -> Outbound webhooks. Exposes the
// existing NotificationsConfig.Webhooks (List<WebhookTarget>) as a multi-item list
// editor (the current TUI only surfaces one). Per webhook: Name, URL, a single
// Authorization-style header; Format is auto-detected from the URL (read-only).
// Delivery policy (dedup/retries/timeout) is intentionally left parked.

import { store } from '../mock/store.js';

const fmt = (url) => (/hooks\.slack\.com/i.test(url) ? 'Slack' : 'Generic');
const check = (b) => (b ? '✓' : ' ');

const FIELDS = [
  { key: 'name', label: 'Name', placeholder: 'pagerduty (optional)', password: false },
  { key: 'url', label: 'URL', placeholder: 'https://hooks.slack.com/services/…', password: false },
  { key: 'header', label: 'Auth header', placeholder: 'Authorization: Bearer … (optional)', password: true },
];

export const configWebhooks = {
  id: 'config-webhooks',
  state: {},
  init() { this.state = { screen: 'list', listIndex: 0, editingId: null, form: { name: '', url: '', header: '' }, field: 0 }; },
  isAnimating() { return this.state.screen === 'form'; },

  list() { return store.telemetry.webhooks; },
  rows() { return [...this.list().map((w) => ({ kind: 'wh', w })), { kind: 'add' }, { kind: 'done' }]; },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Outbound Webhooks');
    if (this.state.screen === 'list') this.rList(scr, r, rt, W);
    else this.rForm(scr, r, rt, W);
  },

  rList(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Outbound Webhooks');
    W.helpLines(scr, r, r.y + 1, ['Operational alerts are POSTed to each enabled target. Slack URLs use Slack format automatically.']);
    const rows = this.rows();
    let yy = r.y + 3;
    if (this.list().length === 0) { scr.text(r.x + 2, yy, 'No outbound webhooks configured yet.', { fg: 'dim' }); yy += 2; }
    rows.forEach((row, i) => {
      const focused = i === this.state.listIndex;
      const line = row.kind === 'wh'
        ? `[${check(row.w.enabled)}] ${row.w.name.padEnd(14)} ${row.w.url.padEnd(38)} ${fmt(row.w.url)}`
        : row.kind === 'add' ? '+ Add webhook' : 'Done';
      const dim = row.kind === 'wh' && !row.w.enabled;
      if (focused) { scr.fillRect(r.x, yy, r.w, 1, ' ', { bg: 'accent', fg: 'onAccent' }); scr.text(r.x, yy, line, { bg: 'accent', fg: 'onAccent' }); }
      else scr.text(r.x, yy, line, { fg: dim ? 'faint' : 'text' });
      yy += 1;
    });
    const row = rows[this.state.listIndex];
    W.helpLines(scr, r, yy + 1, [row?.kind === 'wh'
      ? `${fmt(row.w.url)} format · ${row.w.header ? 'auth header set' : 'no auth header'} · ${row.w.enabled ? 'enabled' : 'disabled'}`
      : 'Add, edit, toggle, or remove outbound alert targets.']);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Edit/Add  [Space] Toggle  [Bksp] Remove  [Esc] Telemetry  [Ctrl+Q] Quit');
  },

  rForm(scr, r, rt, W) {
    const s = this.state;
    W.heading(scr, r, r.y, s.editingId ? `Edit webhook: ${s.form.name || '(unnamed)'}` : 'Add outbound webhook');
    let yy = r.y + 2;
    FIELDS.forEach((f, i) => {
      W.textInputPanel(scr, r, yy, f.label, s.form[f.key], { password: f.password, placeholder: f.placeholder, focused: i === s.field, width: 56 });
      yy += 4;
    });
    W.line(scr, r, yy, `Format:  ${fmt(s.form.url)} (auto-detected from URL)`, 'dim');
    W.helpLines(scr, r, yy + 2, ['URL is required. Auth header is optional and stored masked.']);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓ or Tab] Fields  [Type] Edit  [Enter] Save  [Esc] Back  [Ctrl+Q] Quit');
  },

  save(rt) {
    const s = this.state;
    if (!s.form.url.trim()) { rt.setStatus('URL is required.', 'err'); return; }
    const name = s.form.name.trim() || `${fmt(s.form.url).toLowerCase()}-webhook`;
    if (s.editingId) {
      const w = this.list().find((x) => x.id === s.editingId);
      Object.assign(w, { name, url: s.form.url.trim(), header: s.form.header.trim() });
      rt.setStatus(`Webhook ${name} updated. Saved.`, 'ok');
    } else {
      store.telemetry.webhooks.push({ id: store.telemetry.nextWebhookId++, name, url: s.form.url.trim(), header: s.form.header.trim(), enabled: true });
      rt.setStatus(`Webhook ${name} added. Saved.`, 'ok');
    }
    s.screen = 'list';
  },

  onKey(k, rt) {
    const s = this.state;
    if (s.screen === 'list') {
      const rows = this.rows(); const row = rows[s.listIndex];
      if (k === 'up') s.listIndex = Math.max(0, s.listIndex - 1);
      else if (k === 'down') s.listIndex = Math.min(rows.length - 1, s.listIndex + 1);
      else if (k === 'space' && row.kind === 'wh') { row.w.enabled = !row.w.enabled; rt.setStatus(`${row.w.name} ${row.w.enabled ? 'enabled' : 'disabled'}. Saved.`, 'ok'); }
      else if (k === 'backspace' && row.kind === 'wh') { const n = row.w.name; store.telemetry.webhooks = this.list().filter((w) => w.id !== row.w.id); s.listIndex = Math.max(0, s.listIndex - 1); rt.setStatus(`Removed ${n}. Saved.`, 'ok'); }
      else if (k === 'enter') {
        if (row.kind === 'wh') { s.editingId = row.w.id; s.form = { name: row.w.name, url: row.w.url, header: row.w.header }; s.field = 0; s.screen = 'form'; rt.setStatus(null); }
        else if (row.kind === 'add') { s.editingId = null; s.form = { name: '', url: '', header: '' }; s.field = 0; s.screen = 'form'; rt.setStatus(null); }
        else rt.back();
      } else if (k === 'escape') rt.back();
    } else {
      const f = FIELDS[s.field].key;
      if (k === 'up' || k === 'shift+tab') s.field = (s.field + FIELDS.length - 1) % FIELDS.length;
      else if (k === 'down' || k === 'tab') s.field = (s.field + 1) % FIELDS.length;
      else if (k === 'enter') this.save(rt);
      else if (k === 'escape') { s.screen = 'list'; rt.setStatus(null); }
      else if (k === 'backspace') s.form[f] = s.form[f].slice(0, -1);
      else if (k === 'space') s.form[f] += ' ';
      else if (k.length === 1) s.form[f] += k;
    }
  },
};
