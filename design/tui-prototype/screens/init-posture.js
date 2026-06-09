// screens/init-posture.js
// Fidelity reference: reproduces tests/smoke/screenshots/wizard-security-posture.approved.png

import { initCtx, FEATURE_DEFAULTS } from '../mock/initctx.js';

const ITEMS = [
  '1. Personal — Only you on this machine',
  '2. Team — Shared with trusted teammates',
  '3. Public — Open to untrusted users',
];
const POSTURES = ['Personal', 'Team', 'Public'];

const HELP = [
  'Personal = full shell + tools. Team = no shell, shared tools.',
  '',
  'Public = minimal tools, restricted filesystem.',
  '',
  'This sets the default trust level. You can override per-channel in the Channels step.',
  'Personal mode enables shell with approval gates — commands require user sign-off on first use.',
];

export const securityPosture = {
  id: 'init-posture',
  state: { index: 0 },

  init() { this.state.index = 0; },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Netclaw Setup');
    W.stepIndicator(scr, r, { step: 3, total: 5, title: 'Security Posture', pct: 60 });
    W.heading(scr, r, r.y + 2, 'Who will interact with this Netclaw instance?');
    const after = W.selectionList(scr, r, r.y + 3, ITEMS, this.state.index);
    W.helpLines(scr, r, after + 1, HELP);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Next  [Esc] Back  [Ctrl+Q] Quit');
  },

  onKey(k, rt) {
    if (k === 'up') this.state.index = Math.max(0, this.state.index - 1);
    else if (k === 'down') this.state.index = Math.min(ITEMS.length - 1, this.state.index + 1);
    else if (k === 'enter') {
      initCtx.posture = POSTURES[this.state.index];
      if (initCtx.posture === 'Personal') rt.go('init-health'); // Personal skips Enabled Features
      else { initCtx.features = { ...FEATURE_DEFAULTS[initCtx.posture] }; rt.go('init-features'); }
    } else if (k === 'escape') rt.back();
  },
};
