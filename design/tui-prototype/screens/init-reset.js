// screens/init-reset.js — Init.E2: start-over scope + double confirmation.
// Reset scope chooser, then a two-stage confirm (default Cancel) before any
// destructive action (simplify-netclaw-init: explicit, double-confirmed reset).

const SCOPES = [
  ['Reset setup only', 'Re-run setup; keep memory, sessions, and skills.', 'setup'],
  ['Full reset', 'Delete ALL Netclaw data: config, memory, sessions, secrets.', 'full'],
  ['Cancel', 'Go back without changing anything.', 'cancel'],
];

export const initReset = {
  id: 'init-reset',
  state: {},
  init() { this.state = { phase: 'scope', index: 0, scope: 'setup', confirm: 0 }; },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Netclaw Setup');
    const s = this.state;
    if (s.phase === 'scope') {
      W.heading(scr, r, r.y + 1, 'Start over from scratch — choose a scope:');
      const after = W.selectionList(scr, r, r.y + 3, SCOPES.map(([l]) => l), s.index, s.index === 1 ? { barBg: 'err', barFg: 'base' } : {});
      W.helpLines(scr, r, after + 1, [SCOPES[s.index][1]]);
      W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Back  [Ctrl+Q] Quit');
    } else {
      const full = s.scope === 'full';
      const n = s.phase === 'confirm1' ? 1 : 2;
      W.line(scr, r, r.y + 1, `⚠  ${full ? 'Full reset' : 'Reset setup'} — confirmation ${n} of 2`, 'warn');
      W.helpLines(scr, r, r.y + 3, full
        ? ['This permanently deletes config, memory, sessions, and secrets.', 'This cannot be undone.']
        : ['This re-runs setup. Memory, sessions, and skills are kept.']);
      W.selectionList(scr, r, r.y + 6, ['Cancel', `Yes, ${full ? 'delete everything' : 'reset setup'}`], s.confirm, s.confirm === 1 ? { barBg: 'err', barFg: 'base' } : {});
      W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Back  [Ctrl+Q] Quit');
    }
  },

  onKey(k, rt) {
    const s = this.state;
    if (s.phase === 'scope') {
      if (k === 'up') s.index = Math.max(0, s.index - 1);
      else if (k === 'down') s.index = Math.min(SCOPES.length - 1, s.index + 1);
      else if (k === 'enter') { const t = SCOPES[s.index][2]; if (t === 'cancel') rt.back(); else { s.scope = t; s.phase = 'confirm1'; s.confirm = 0; } }
      else if (k === 'escape') rt.back();
    } else {
      if (k === 'up') s.confirm = Math.max(0, s.confirm - 1);
      else if (k === 'down') s.confirm = Math.min(1, s.confirm + 1);
      else if (k === 'enter') {
        if (s.confirm === 0) { s.phase = 'scope'; s.confirm = 0; }              // Cancel -> back to scope
        else if (s.phase === 'confirm1') { s.phase = 'confirm2'; s.confirm = 0; } // first Yes -> second confirm
        else { rt.replace('init-provider'); }                                    // confirmed -> fresh setup
      } else if (k === 'escape') { s.phase = s.phase === 'confirm2' ? 'confirm1' : 'scope'; s.confirm = 0; }
    }
  },
};
