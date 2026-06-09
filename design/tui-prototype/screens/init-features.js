// screens/init-features.js — simplified init, Step 4 of 5: Enabled Features.
// Shown only for Team/Public (Personal skips to Health Check). Defaults are seeded
// by posture when the step is entered (see init-posture). Mirrors FeatureSelectionStepView.

import { initCtx } from '../mock/initctx.js';
import { FEATURE_DESC } from '../mock/store.js';

const FEATURES = ['Memory', 'Search', 'Skills', 'Scheduling', 'SubAgents', 'Webhooks'];
const check = (b) => (b ? '✓' : ' ');

export const initFeatures = {
  id: 'init-features',
  state: { index: 0 },
  init() { this.state.index = 0; },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Netclaw Setup');
    W.stepIndicator(scr, r, { step: 4, total: 5, title: 'Enabled Features', pct: 80 });
    W.heading(scr, r, r.y + 2, 'Select which features to enable for this deployment:');
    const rows = FEATURES.map((n) => `[${check(initCtx.features[n])}] ${n.padEnd(12)} ${FEATURE_DESC[n]}`);
    const after = W.selectionList(scr, r, r.y + 4, rows, this.state.index, { disabled: (i) => !initCtx.features[FEATURES[i]] });
    const lines = ['Space to toggle, Enter to continue.'];
    if (initCtx.posture === 'Public') lines.push('', 'Note: enabling Search only enables the runtime. Public sessions still require explicit tool allowlisting for web_search/web_fetch.');
    W.helpLines(scr, r, after + 1, lines);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Space] Toggle  [Enter] Next  [Esc] Back  [Ctrl+Q] Quit');
  },

  onKey(k, rt) {
    const s = this.state;
    if (k === 'up') s.index = Math.max(0, s.index - 1);
    else if (k === 'down') s.index = Math.min(FEATURES.length - 1, s.index + 1);
    else if (k === 'space') { const n = FEATURES[s.index]; initCtx.features[n] = !initCtx.features[n]; }
    else if (k === 'enter') rt.go('init-health');
    else if (k === 'escape') rt.back();
  },
};
