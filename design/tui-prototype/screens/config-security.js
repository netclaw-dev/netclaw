// screens/config-security.js
//
// `netclaw config` -> Security & Access. Mirrors SecurityAccessPage's mode switch:
//   menu / posture / features / audienceList / audienceProfile. Exposure Mode is a
//   routed handoff to its own page (config-exposure).
//
// Selection style is unified on init's full-width bar (the real code mixes a bar on
// the dashboard with a ▶-marker here). Autosave: every toggle/cycle/reset persists
// to the mock store immediately with a "Saved." status; Esc walks back up the modes
// (then to the dashboard) with state intact.

import {
  store, enabledCount, FEATURES, FEATURE_DESC,
  AUDIENCES, AUDIENCE_DESC, FILE_SCOPES, ATTACHMENT_LEVELS, resetAudience,
} from '../mock/store.js';

const MENU = [
  ['Security Posture', () => store.posture, 'Deployment trust stance.', 'posture'],
  ['Enabled Features', () => `${enabledCount()} of 6 on`, 'Deployment-wide runtime feature gates.', 'features'],
  ['Audience Profiles', () => (AUDIENCES.some((a) => store.audienceProfiles[a].customized) ? 'Customized' : 'No overrides'), 'Curated per-audience access rules.', 'audience'],
  ['Exposure Mode', () => store.exposureMode || 'Local', 'Daemon reachability and tunnel topology.', 'exposure'],
];

const POSTURES = [
  ['Personal', 'Just me. Local-only by default. Tools have wide access.'],
  ['Team', 'Small team via Slack/Discord. Audience-restricted tools.'],
  ['Public', 'Open to untrusted users. Strict defaults and access controls.'],
];

// Per-audience editor rows (mirrors AudienceProfileRow). `section` starts a group.
const PROFILE_ROWS = [
  { kind: 'toggle', key: 'fileTools', label: 'File tools', section: 'Tools', help: 'File tools grant read/list/attach/write/edit; File scope below limits where they can operate.' },
  { kind: 'toggle', key: 'web', label: 'Web', help: 'Web grants web_search and web_fetch for this audience.' },
  { kind: 'toggle', key: 'skills', label: 'Skills', help: 'Skills grants skill management and loading tools for this audience.' },
  { kind: 'toggle', key: 'scheduling', label: 'Scheduling', help: 'Scheduling grants reminder create/list/cancel/history tools.' },
  { kind: 'toggle', key: 'changeWorkspace', label: 'Change workspace', help: 'Change workspace lets sessions switch workspace roots.' },
  { kind: 'cycle', key: 'fileScope', label: 'File scope', section: 'Access', help: 'File scope limits where file tools can operate for this audience.' },
  { kind: 'cycle', key: 'attachments', label: 'Attachments', help: 'Accepted inbound channel attachment types for this audience.' },
  { kind: 'open', label: 'MCP grants', value: 'netclaw mcp permissions', help: 'MCP server and per-tool grants are managed in the dedicated MCP permissions editor.' },
  { kind: 'reset', label: 'Reset overrides', section: 'Actions', help: 'Reset overrides restores this audience to the current posture baseline, including hidden MCP and approval settings.' },
];

const check = (b) => (b ? '✓' : ' ');
const cyc = (val) => `[◀ ${val.padEnd(17)} ▶]`;

export const configSecurity = {
  id: 'config-security',
  state: {},

  init() {
    this.state = {
      mode: 'menu', menuIndex: 0,
      featureIndex: 0,
      postureIndex: Math.max(0, POSTURES.findIndex(([l]) => l === store.posture)),
      audienceIndex: 0, audience: 'Personal', rowIndex: 0,
    };
  },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Security & Access');
    ({
      menu: this.renderMenu, posture: this.renderPosture, features: this.renderFeatures,
      audience: this.renderAudienceList, audienceProfile: this.renderAudienceProfile,
    }[this.state.mode]).call(this, scr, r, rt, W);
  },

  renderMenu(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Security & Access');
    const rows = MENU.map(([label, summary]) => `${label.padEnd(20)}  ${summary()}`);
    const after = W.selectionList(scr, r, r.y + 1, rows, this.state.menuIndex);
    W.helpLines(scr, r, after + 1, [MENU[this.state.menuIndex][2]]);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Open  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderPosture(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Security Posture');
    W.helpLines(scr, r, r.y + 1, [`Current posture: ${store.posture}`]);
    const rows = POSTURES.map(([label, desc]) => `[${check(label === store.posture)}] ${label.padEnd(10)} ${desc}`);
    W.selectionList(scr, r, r.y + 3, rows, this.state.postureIndex);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Apply  [Esc] Security & Access  [Ctrl+Q] Quit');
  },

  renderFeatures(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Enabled Features');
    W.helpLines(scr, r, r.y + 1, ['Toggle global runtime features. Audience exposure is configured separately.']);
    const rows = FEATURES.map((name) => `[${check(store.features[name])}] ${name.padEnd(12)} ${FEATURE_DESC[name]}`);
    W.selectionList(scr, r, r.y + 3, rows, this.state.featureIndex, { disabled: (i) => !store.features[FEATURES[i]] });
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Space/Enter] Toggle/Save  [Esc] Security & Access  [Ctrl+Q] Quit');
  },

  renderAudienceList(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Audience Profiles');
    W.helpLines(scr, r, r.y + 1, [
      `System default posture: ${store.posture}`,
      'Customize audience/channel access when it should differ.',
      '* global default audience    Customized = custom overrides',
    ]);
    const rows = AUDIENCES.map((a) => {
      const def = a === store.posture ? '*' : ' ';
      const mark = store.audienceProfiles[a].customized ? 'Customized' : '';
      return `${def} ${a.padEnd(9)} ${AUDIENCE_DESC[a].padEnd(34)} ${mark}`;
    });
    W.selectionList(scr, r, r.y + 5, rows, this.state.audienceIndex);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Edit Audience  [Esc] Security & Access  [Ctrl+Q] Quit');
  },

  renderAudienceProfile(scr, r, rt, W) {
    const aud = this.state.audience;
    const prof = store.audienceProfiles[aud];
    W.heading(scr, r, r.y, `Audience Profile: ${aud}`);
    W.helpLines(scr, r, r.y + 1, [
      `System default posture: ${store.posture}`,
      `Profile: ${prof.customized ? 'Customized overrides' : 'No custom overrides'}`,
    ]);

    let yy = r.y + 4;
    PROFILE_ROWS.forEach((row, i) => {
      if (row.section) {
        if (i > 0) yy += 1; // blank before a new section group
        scr.text(r.x + 2, yy, row.section, { fg: 'text', bold: true });
        yy += 1;
      }
      const line = row.kind === 'toggle' ? `[${check(prof[row.key])}] ${row.label}`
        : row.kind === 'cycle' ? `${row.label.padEnd(14)} ${cyc(prof[row.key])}`
        : row.kind === 'open' ? `${row.label.padEnd(14)} [Open] ${row.value}`
        : `${row.label.padEnd(14)} [Reset]`;
      const focused = i === this.state.rowIndex;
      const dim = row.kind === 'toggle' && !prof[row.key];
      if (focused) {
        scr.fillRect(r.x, yy, r.w, 1, ' ', { bg: 'accent', fg: 'onAccent' });
        scr.text(r.x, yy, line, { bg: 'accent', fg: 'onAccent' });
      } else {
        scr.text(r.x, yy, line, { fg: dim ? 'faint' : 'text' });
      }
      yy += 1;
    });
    W.helpLines(scr, r, yy + 1, [PROFILE_ROWS[this.state.rowIndex].help]);

    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [←/→] Change  [Space/Enter] Toggle/Apply  [Esc] Audiences  [Ctrl+Q] Quit');
  },

  // ── cycle a value option list, persist, and report ──
  cycle(rt, dir) {
    const aud = this.state.audience;
    const prof = store.audienceProfiles[aud];
    const row = PROFILE_ROWS[this.state.rowIndex];
    if (row.kind !== 'cycle') return;
    const opts = row.key === 'fileScope' ? FILE_SCOPES[aud] : ATTACHMENT_LEVELS;
    const idx = Math.max(0, opts.indexOf(prof[row.key]));
    prof[row.key] = opts[(idx + dir + opts.length) % opts.length];
    prof.customized = true;
    const what = row.key === 'fileScope' ? 'file access' : 'attachments';
    rt.setStatus(`${aud} ${what} set to ${prof[row.key]}. Saved.`, 'ok');
  },

  onKey(k, rt) {
    const s = this.state;
    if (s.mode === 'menu') {
      if (k === 'up') { s.menuIndex = Math.max(0, s.menuIndex - 1); rt.setStatus(null); }
      else if (k === 'down') { s.menuIndex = Math.min(MENU.length - 1, s.menuIndex + 1); rt.setStatus(null); }
      else if (k === 'enter') {
        const target = MENU[s.menuIndex][3];
        if (target === 'exposure') rt.go('config-exposure');
        else { s.mode = target; rt.setStatus(null); if (target === 'audience') s.audienceIndex = 0; }
      } else if (k === 'escape') rt.back();
    } else if (s.mode === 'posture') {
      if (k === 'up') s.postureIndex = Math.max(0, s.postureIndex - 1);
      else if (k === 'down') s.postureIndex = Math.min(POSTURES.length - 1, s.postureIndex + 1);
      else if (k === 'enter') { store.posture = POSTURES[s.postureIndex][0]; rt.setStatus(`Posture set to ${store.posture}. Saved.`, 'ok'); }
      else if (k === 'escape') { s.mode = 'menu'; rt.setStatus(null); }
    } else if (s.mode === 'features') {
      if (k === 'up') s.featureIndex = Math.max(0, s.featureIndex - 1);
      else if (k === 'down') s.featureIndex = Math.min(FEATURES.length - 1, s.featureIndex + 1);
      else if (k === 'space' || k === 'enter') {
        const name = FEATURES[s.featureIndex];
        store.features[name] = !store.features[name];
        rt.setStatus(`${name} ${store.features[name] ? 'enabled' : 'disabled'}. Saved.`, 'ok');
      } else if (k === 'escape') { s.mode = 'menu'; rt.setStatus(null); }
    } else if (s.mode === 'audience') {
      if (k === 'up') s.audienceIndex = Math.max(0, s.audienceIndex - 1);
      else if (k === 'down') s.audienceIndex = Math.min(AUDIENCES.length - 1, s.audienceIndex + 1);
      else if (k === 'enter') { s.audience = AUDIENCES[s.audienceIndex]; s.rowIndex = 0; s.mode = 'audienceProfile'; rt.setStatus(null); }
      else if (k === 'escape') { s.mode = 'menu'; rt.setStatus(null); }
    } else if (s.mode === 'audienceProfile') {
      const prof = store.audienceProfiles[s.audience];
      const row = PROFILE_ROWS[s.rowIndex];
      if (k === 'up') s.rowIndex = Math.max(0, s.rowIndex - 1);
      else if (k === 'down') s.rowIndex = Math.min(PROFILE_ROWS.length - 1, s.rowIndex + 1);
      else if (k === 'left') this.cycle(rt, -1);
      else if (k === 'right') this.cycle(rt, 1);
      else if (k === 'space' || k === 'enter') {
        if (row.kind === 'toggle') {
          prof[row.key] = !prof[row.key]; prof.customized = true;
          rt.setStatus(`${s.audience} ${row.label} ${prof[row.key] ? 'enabled' : 'disabled'}. Saved.`, 'ok');
        } else if (row.kind === 'cycle') this.cycle(rt, 1);
        else if (row.kind === 'open') rt.setStatus('Opens `netclaw mcp permissions` (routed handoff).', 'dim');
        else { resetAudience(s.audience); rt.setStatus(`${s.audience} overrides reset to the ${store.posture} posture baseline.`, 'ok'); }
      } else if (k === 'escape') { s.mode = 'audience'; rt.setStatus(null); }
    }
  },
};
