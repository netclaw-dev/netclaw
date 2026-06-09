// screens/init-existing.js — Init.E1: existing-install menu.
// When `netclaw init` detects an existing install it offers an explicit action
// menu instead of refusing or silently re-running (simplify-netclaw-init).

const ITEMS = [
  ['Redo identity setup', 'Re-run just the identity step; provider and settings are kept.', 'identity'],
  ['Open configuration editor', 'Adjust settings in `netclaw config` instead.', 'config'],
  ['Start over from scratch', 'Reset and run the whole setup again.', 'reset'],
  ['Cancel', 'Leave everything as-is and exit.', 'cancel'],
];

export const initExisting = {
  id: 'init-existing',
  state: { index: 0 },
  init() { this.state.index = 0; },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Netclaw Setup');
    W.heading(scr, r, r.y + 1, 'Existing Netclaw install detected.');
    W.helpLines(scr, r, r.y + 2, ['Your current config is untouched until you confirm an action.']);
    const after = W.selectionList(scr, r, r.y + 4, ITEMS.map(([l]) => l), this.state.index);
    W.helpLines(scr, r, after + 1, [ITEMS[this.state.index][1]]);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Quit  [Ctrl+Q] Quit');
  },

  onKey(k, rt) {
    const s = this.state;
    if (k === 'up') { s.index = Math.max(0, s.index - 1); rt.setStatus(null); }
    else if (k === 'down') { s.index = Math.min(ITEMS.length - 1, s.index + 1); rt.setStatus(null); }
    else if (k === 'enter') {
      const t = ITEMS[s.index][2];
      if (t === 'identity') rt.go('init-identity');
      else if (t === 'config') rt.go('config-dashboard');
      else if (t === 'reset') rt.go('init-reset');
      else rt.setStatus('(prototype) would exit `netclaw init` and leave config unchanged.', 'dim');
    }
  },
};
