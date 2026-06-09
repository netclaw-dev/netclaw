// screens/config-exposure.js
//
// `netclaw config` -> Security & Access -> Exposure Mode (routed handoff). Mirrors
// ExposureModeStepView: a mode picker that branches into mode-specific sub-forms —
// the canonical "small variations" wart (one editor, five very different shapes).
//   Local            -> save
//   Reverse Proxy    -> bind address -> trusted proxies -> notice -> save
//   Tailscale Serve  -> notice -> save
//   Funnel/Cloudflare-> high-risk warning -> save
//
// Inactive-value retention is demonstrated: a reverse-proxy host typed once is kept
// in the store and re-seeded even after switching to another mode.

import { store } from '../mock/store.js';

const PORT = 5199;

const MODES = [
  { value: 'Local', label: 'Local — loopback only, safest (recommended)' },
  { value: 'Reverse Proxy', label: 'Reverse Proxy — behind nginx, Caddy, Traefik, IIS, ALB, etc.' },
  { value: 'Tailscale Serve', label: 'Tailscale Serve — accessible within your tailnet' },
  { value: 'Tailscale Funnel', label: 'Tailscale Funnel — public internet ⚠' },
  { value: 'Cloudflare Tunnel', label: 'Cloudflare Tunnel — public internet ⚠' },
];

const LOOPBACK = ['127.0.0.1', 'localhost', '::1'];

const RISK_REQS = {
  'Tailscale Funnel': [
    'Hub authentication is configured (device pairing or bearer token)',
    '`tailscaled` is running and Funnel is explicitly enabled for this service',
    'You trust your security posture selection',
  ],
  'Cloudflare Tunnel': [
    'Hub authentication is configured (device pairing or bearer token)',
    '`cloudflared` is running and Cloudflare Access protects the tunnel',
    'You trust your security posture selection',
  ],
};

export const configExposure = {
  id: 'config-exposure',
  state: {},

  init() {
    this.state = {
      screen: 'modes',
      modeIndex: Math.max(0, MODES.findIndex((m) => m.value === store.exposureMode)),
      chosen: null, host: '', proxies: '', error: null,
    };
  },

  isAnimating() { return this.state.screen === 'rp-host' || this.state.screen === 'rp-proxies'; },

  // render() is assigned at the bottom of the file (dispatches by screen).

  // ── renderers ──
  renderModes(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'How will this Netclaw daemon be accessed?');
    const rows = MODES.map((m) => `[${m.value === store.exposureMode ? 'x' : ' '}] ${m.label}`);
    const after = W.selectionList(scr, r, r.y + 2, rows, this.state.modeIndex);
    W.helpLines(scr, r, after + 1, [
      '[x] active exposure mode',
      '',
      '⚠ = exposes daemon beyond this machine. Ensure auth is configured first.',
    ]);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Continue  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderRpHost(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Reverse proxy: bind address');
    W.helpLines(scr, r, r.y + 1, [
      'Daemon will listen on this address. Loopback (127.0.0.1, ::1, localhost)',
      'is not allowed — loopback auto-auth cannot be inherited through a proxy.',
    ]);
    W.textInputPanel(scr, r, r.y + 4, 'Bind address', this.state.host, { placeholder: '0.0.0.0', focused: true, width: 40 });
    if (this.state.error) W.line(scr, r, r.y + 8, `✗ ${this.state.error}`, 'err');
    W.keyHints(scr, r, '[Enter] Continue  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderRpProxies(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Reverse proxy: trusted proxies');
    W.helpLines(scr, r, r.y + 1, [
      'Comma-separated IP addresses or CIDR ranges. Forwarded headers from any',
      'other source will be ignored.',
    ]);
    W.textInputPanel(scr, r, r.y + 4, 'Trusted proxies', this.state.proxies, { placeholder: '10.0.0.0/24, 192.168.1.5', focused: true, width: 60 });
    const n = this.state.proxies.split(',').map((x) => x.trim()).filter(Boolean).length;
    W.line(scr, r, r.y + 8, n === 0
      ? 'At least one IP or CIDR is required — the daemon will not start without it.'
      : `${n} trusted proxy entr${n === 1 ? 'y' : 'ies'} captured. Press Enter to continue.`,
      n === 0 ? 'warn' : 'faint');
    W.keyHints(scr, r, '[Enter] Continue  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderRpNotice(scr, r, rt, W) {
    W.line(scr, r, r.y, 'Reverse proxy configured', 'accent');
    W.line(scr, r, r.y + 2, `Daemon listen address:    http://${this.state.host || '0.0.0.0'}:${PORT}`, 'fg');
    W.line(scr, r, r.y + 3, `Trusted proxies:          ${this.state.proxies || '(none)'}`, 'fg');
    W.helpLines(scr, r, r.y + 5, [
      'You are responsible for:',
      '  • Terminating TLS at the proxy',
      '  • Restricting inbound access at the proxy / firewall',
      '  • Setting X-Forwarded-For and X-Forwarded-Proto correctly',
    ]);
    W.selectionList(scr, r, r.y + 11, ['Got it — continue'], 0);
    W.keyHints(scr, r, '[Enter] Save  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderTsNotice(scr, r, rt, W) {
    W.line(scr, r, r.y, 'Tailscale Serve: daemon accessible within your tailnet only.', 'accent');
    W.helpLines(scr, r, r.y + 2, [
      'Devices on your tailnet can reach the daemon. Not reachable from the public internet.',
      'Ensure `tailscaled` is running before starting Netclaw.',
    ]);
    W.selectionList(scr, r, r.y + 5, ['Got it — continue'], 0);
    W.keyHints(scr, r, '[Enter] Save  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderRisk(scr, r, rt, W) {
    const mode = this.state.chosen;
    W.line(scr, r, r.y, `⚠  ${mode} exposes your daemon to the public internet.`, 'warn');
    W.line(scr, r, r.y + 2, 'Before proceeding, ensure:', 'fg');
    (RISK_REQS[mode] || []).forEach((req, i) => W.line(scr, r, r.y + 3 + i, `  • ${req}`, 'faint'));
    W.selectionList(scr, r, r.y + 7, ['I understand the risks — continue'], 0, { barBg: 'warn', barFg: 'base' });
    W.keyHints(scr, r, '[Enter] Save  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderSaved(scr, r, rt, W) {
    W.line(scr, r, r.y, `✓ ${store.exposureMode} exposure mode saved.`, 'ok');
    W.helpLines(scr, r, r.y + 2, ['Inactive mode settings are preserved for later. Press Enter to return to Security & Access.']);
    W.keyHints(scr, r, '[Enter] Security & Access  [Esc] Review modes  [Ctrl+Q] Quit');
  },

  commit(mode) {
    store.exposureMode = mode;
    if (mode === 'Reverse Proxy') { store.rpHost = this.state.host; store.rpProxies = this.state.proxies; }
    this.state.screen = 'saved';
  },

  onKey(k, rt) {
    const s = this.state;
    switch (s.screen) {
      case 'modes':
        if (k === 'up') s.modeIndex = Math.max(0, s.modeIndex - 1);
        else if (k === 'down') s.modeIndex = Math.min(MODES.length - 1, s.modeIndex + 1);
        else if (k === 'enter') {
          s.chosen = MODES[s.modeIndex].value;
          if (s.chosen === 'Local') this.commit('Local');
          else if (s.chosen === 'Reverse Proxy') { s.host = store.rpHost; s.proxies = store.rpProxies; s.error = null; s.screen = 'rp-host'; }
          else if (s.chosen === 'Tailscale Serve') s.screen = 'ts-notice';
          else s.screen = 'risk';
        } else if (k === 'escape') rt.back();
        break;
      case 'rp-host':
        if (k === 'enter') {
          const host = s.host.trim() || '0.0.0.0';
          if (LOOPBACK.includes(host)) s.error = `'${host}' is loopback — not allowed for reverse-proxy mode. Use a non-loopback bind address (e.g. 0.0.0.0).`;
          else { s.host = host; s.error = null; s.screen = 'rp-proxies'; }
        } else if (k === 'escape') s.screen = 'modes';
        else if (k === 'backspace') s.host = s.host.slice(0, -1);
        else if (k.length === 1) s.host += k;
        break;
      case 'rp-proxies':
        if (k === 'enter') s.screen = 'rp-notice';
        else if (k === 'escape') s.screen = 'rp-host';
        else if (k === 'backspace') s.proxies = s.proxies.slice(0, -1);
        else if (k === 'space') s.proxies += ' ';
        else if (k.length === 1) s.proxies += k;
        break;
      case 'rp-notice': if (k === 'enter') this.commit('Reverse Proxy'); else if (k === 'escape') s.screen = 'rp-proxies'; break;
      case 'ts-notice': if (k === 'enter') this.commit('Tailscale Serve'); else if (k === 'escape') s.screen = 'modes'; break;
      case 'risk': if (k === 'enter') this.commit(s.chosen); else if (k === 'escape') s.screen = 'modes'; break;
      case 'saved': if (k === 'enter') rt.back(); else if (k === 'escape') s.screen = 'modes'; break;
    }
  },
};

// Dispatch render by screen (kept out of the object literal for readability).
configExposure.render = function (scr, rt, W) {
  const r = W.pageFrame(scr, 'Exposure Mode');
  ({
    modes: this.renderModes, 'rp-host': this.renderRpHost, 'rp-proxies': this.renderRpProxies,
    'rp-notice': this.renderRpNotice, 'ts-notice': this.renderTsNotice, risk: this.renderRisk, saved: this.renderSaved,
  }[this.state.screen]).call(this, scr, r, rt, W);
};
