// screens/config-search.js
//
// `netclaw config` -> Search. Mirrors SearchConfigEditorPage's screen machine and
// extends it with PROBE-DRIVEN DISCLOSURE for SearXNG: whether an API key is
// required is a runtime property of the instance, not a static field flag. So we
// ask for the Base URL, probe, and branch on the probe REASON:
//   ok           -> saved
//   auth-required -> reveal a Base URL + API key form (the key appears only now)
//   unreachable   -> the generic Retry/Back/Save-anyway warning dialog
//
// The two-field auth form is navigated with ↑/↓ (consistent with the rest of
// config) AND Tab/Shift+Tab as aliases — no separate "form mode".
//
// Effects are faked: SearXNG with no key -> auth-required; with a key -> success.

import { store } from '../mock/store.js';

const BACKENDS = [
  { value: 'duckduckgo', label: 'DuckDuckGo', field: null,
    desc: 'DuckDuckGo works without setup, but may hit bot detection.' },
  { value: 'brave', label: 'Brave', desc: 'Brave Search API — fast and private; requires an API key.',
    field: { title: 'Brave Search requires an API key.', label: 'API Key', password: true,
      placeholder: 'Enter Brave API key...', hint: 'Stored in secrets.json. Get a key at search.brave.com/app/keys.' } },
  { value: 'searxng', label: 'SearXNG', desc: 'Self-hosted SearXNG metasearch — point at your instance URL.',
    field: { title: 'Enter the base URL of your SearXNG instance.', label: 'Base URL', password: false,
      placeholder: 'https://searx.example.org', hint: 'Most instances are open. If yours requires a key, you will be prompted.' } },
];

const saved = new Set(['brave']);
const isConfigured = (v) => v === 'duckduckgo' || saved.has(v);

export const configSearch = {
  id: 'config-search',
  state: {},

  init() {
    this.state = {
      screen: 'provider', providerIndex: 0,
      input: '', keyInput: '', authFieldIndex: 1,
      dialogIndex: 0, probeStart: 0, cameFrom: '', reason: '', saveOk: true,
    };
  },

  isAnimating() {
    const s = this.state;
    return s.screen === 'entry' || s.screen === 'authForm' || s.screen === 'validating';
  },

  get backend() { return BACKENDS[this.state.providerIndex]; },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Search');
    ({
      provider: this.renderProvider, entry: this.renderEntry, validating: this.renderValidating,
      authForm: this.renderAuthForm, dialog: this.renderDialog, saved: this.renderSaved,
    }[this.state.screen]).call(this, scr, r, rt, W);
  },

  renderProvider(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Choose the backend Netclaw uses for web search.');
    const rows = BACKENDS.map((b) =>
      `[${b.value === store.searchBackend ? 'x' : ' '}] ${b.label.padEnd(16)} ${isConfigured(b.value) ? '✓' : ' '}`);
    const after = W.selectionList(scr, r, r.y + 2, rows, this.state.providerIndex);
    W.helpLines(scr, r, after + 1, ['[x] active backend    ✓ backend has saved setup', '', this.backend.desc]);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Continue  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderEntry(scr, r, rt, W) {
    const b = this.backend;
    if (!b.field) {
      W.heading(scr, r, r.y, b.desc);
      W.helpLines(scr, r, r.y + 2, ['Press Enter to validate and save this provider selection.']);
    } else {
      W.heading(scr, r, r.y, b.field.title);
      W.textInputPanel(scr, r, r.y + 2, b.field.label, this.state.input, {
        password: b.field.password, placeholder: b.field.placeholder, focused: true, width: 60,
      });
      W.helpLines(scr, r, r.y + 6, [b.field.hint]);
    }
    W.keyHints(scr, r, '[Enter] Continue  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderValidating(scr, r, rt, W) {
    W.line(scr, r, r.y, 'Validating Search configuration...', 'fg');
    W.spinner(scr, r, r.y + 2, `Probing ${this.backend.label} endpoint...`, 'warn');
    W.helpLines(scr, r, r.y + 4, ['This may take a few seconds.']);
    W.keyHints(scr, r, '[Ctrl+Q] Quit');
  },

  // Probe came back "auth-required": reveal the Base URL + API key together. The key
  // field only exists because the instance demanded it — not a static schema flag.
  renderAuthForm(scr, r, rt, W) {
    const s = this.state;
    W.heading(scr, r, r.y, 'This SearXNG instance requires an API key.');
    W.helpLines(scr, r, r.y + 1, [`Probed ${s.input} → 401 Unauthorized. Add the instance's API key, or fix the URL.`]);
    W.textInputPanel(scr, r, r.y + 3, 'Base URL', s.input, { placeholder: 'https://searx.example.org', focused: s.authFieldIndex === 0, width: 60 });
    W.textInputPanel(scr, r, r.y + 7, 'API key', s.keyInput, { password: true, placeholder: 'Enter the instance API key...', focused: s.authFieldIndex === 1, width: 60 });
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓ or Tab] Move between fields  [Enter] Re-validate  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderDialog(scr, r, rt, W) {
    const bw = r.w - 4, bx = r.x + 2, by = r.y + 1, bh = 11;
    const inner = scr.box(bx, by, bw, bh, { fg: 'warn' }, { border: 'rounded', title: 'Search Validation Warning', titleColor: 'warn' });
    scr.text(inner.x + 2, inner.y + 1, 'Netclaw could not complete a live search using this configuration.', { fg: 'text' });
    const msg = this.state.reason === 'auth'
      ? `Probe to ${this.state.input} failed: 401 Unauthorized — this instance requires an API key.`
      : `Probe to ${this.state.input} failed: the endpoint did not return results (HTTP 502).`;
    scr.text(inner.x + 2, inner.y + 3, msg, { fg: 'yellow' });
    W.selectionList(scr, { x: inner.x + 2, y: inner.y, w: inner.w - 4, h: inner.h }, inner.y + 5,
      ['Retry validation', 'Back to edit', 'Save anyway'], this.state.dialogIndex, { barBg: 'warn', barFg: 'base' });
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Back to edit  [Ctrl+Q] Quit');
  },

  renderSaved(scr, r, rt, W) {
    W.line(scr, r, r.y, this.state.saveOk ? '✓ Search validated and saved.' : '✓ Saved without a successful probe.', 'ok');
    W.helpLines(scr, r, r.y + 2, [`Backend set to ${this.backend.label}. Press Enter to return to Settings Areas.`]);
    W.keyHints(scr, r, '[Enter] Settings Areas  [Esc] Review backends  [Ctrl+Q] Quit');
  },

  // ── transitions ──
  startProbe(rt) {
    const s = this.state;
    s.cameFrom = s.screen;
    s.screen = 'validating';
    s.probeStart = performance.now();
    rt.schedule(2200, () => {
      if (this.backend.value !== 'searxng') { this.commitSaved(rt, true); return; }
      if (s.keyInput.trim()) { this.commitSaved(rt, true); return; }   // key supplied -> ok
      s.reason = 'auth';
      if (s.cameFrom === 'entry') { s.authFieldIndex = 1; s.screen = 'authForm'; }  // first time: reveal the key field
      else { s.dialogIndex = 0; s.screen = 'dialog'; }                              // skipped the key -> warn
    });
  },
  commitSaved(rt, ok) {
    const s = this.state;
    s.saveOk = ok;
    store.searchBackend = this.backend.value;
    saved.add(this.backend.value);
    s.screen = 'saved';
  },

  onKey(k, rt) {
    const s = this.state;
    switch (s.screen) {
      case 'provider':
        if (k === 'up') s.providerIndex = Math.max(0, s.providerIndex - 1);
        else if (k === 'down') s.providerIndex = Math.min(BACKENDS.length - 1, s.providerIndex + 1);
        else if (k === 'enter') { s.input = ''; s.keyInput = ''; s.screen = 'entry'; }
        else if (k === 'escape') rt.back();
        break;
      case 'entry':
        if (k === 'enter') this.startProbe(rt);
        else if (k === 'escape') { rt.clearTimers(); s.screen = 'provider'; }
        else if (this.backend.field) {
          if (k === 'backspace') s.input = s.input.slice(0, -1);
          else if (k === 'space') s.input += ' ';
          else if (k.length === 1) s.input += k;
        }
        break;
      case 'validating':
        if (k === 'escape') { rt.clearTimers(); s.screen = s.cameFrom === 'authForm' ? 'authForm' : 'entry'; }
        break;
      case 'authForm': {
        const field = s.authFieldIndex === 0 ? 'input' : 'keyInput';
        if (k === 'up' || k === 'shift+tab') s.authFieldIndex = (s.authFieldIndex + 1) % 2; // 2 fields: wrap either way
        else if (k === 'down' || k === 'tab') s.authFieldIndex = (s.authFieldIndex + 1) % 2;
        else if (k === 'enter') { rt.setStatus(null); this.startProbe(rt); }
        else if (k === 'escape') { rt.clearTimers(); s.screen = 'entry'; }
        else if (k === 'backspace') s[field] = s[field].slice(0, -1);
        else if (k === 'space') s[field] += ' ';
        else if (k.length === 1) s[field] += k;
        break;
      }
      case 'dialog':
        if (k === 'up') s.dialogIndex = Math.max(0, s.dialogIndex - 1);
        else if (k === 'down') s.dialogIndex = Math.min(2, s.dialogIndex + 1);
        else if (k === 'enter') {
          if (s.dialogIndex === 0) { s.authFieldIndex = 1; s.screen = 'authForm'; }  // Retry -> add the key
          else if (s.dialogIndex === 1) s.screen = 'authForm';                       // Back to edit
          else this.commitSaved(rt, false);                                          // Save anyway
        } else if (k === 'escape') s.screen = 'authForm';
        break;
      case 'saved':
        if (k === 'enter') rt.back();
        else if (k === 'escape') s.screen = 'provider';
        break;
    }
  },
};
