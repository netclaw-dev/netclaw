// screens/init-identity.js — simplified init, Step 2 of 5: Identity.
// Multi-field form navigated with ↑/↓ or Tab (the validated form pattern). On
// re-entry the fields are prefilled from the existing config (secrets stay masked;
// none here). Mirrors the simplified-init Identity step (TUI-003 Init.2).

import { initCtx } from '../mock/initctx.js';

const FIELDS = [
  { key: 'agentName', label: 'Agent name', placeholder: 'netclaw', hint: 'What your agent calls itself in conversations.' },
  { key: 'userName', label: 'Your name', placeholder: 'Ada Lovelace', hint: 'How the agent addresses you.' },
  { key: 'timezone', label: 'Timezone', placeholder: 'America/New_York', hint: 'IANA timezone for schedules and timestamps.' },
  { key: 'workspaces', label: 'Projects directory', placeholder: '~/projects', hint: 'Root Netclaw uses for project discovery and workspace prompts.' },
];

export const initIdentity = {
  id: 'init-identity',
  state: { field: 0 },
  init() { this.state.field = 0; },
  isAnimating() { return true; }, // caret blink

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Netclaw Setup');
    W.stepIndicator(scr, r, { step: 2, total: 5, title: 'Identity', pct: 40 });
    W.heading(scr, r, r.y + 2, 'Tell Netclaw about you and your agent:');
    let yy = r.y + 4;
    FIELDS.forEach((f, i) => {
      W.textInputPanel(scr, r, yy, f.label, initCtx.identity[f.key], { placeholder: f.placeholder, focused: i === this.state.field, width: 48 });
      yy += 4;
    });
    W.helpLines(scr, r, yy, [FIELDS[this.state.field].hint]);
    W.keyHints(scr, r, '[↑/↓ or Tab] Fields  [Type] Edit  [Enter] Next  [Esc] Back  [Ctrl+Q] Quit');
  },

  onKey(k, rt) {
    const s = this.state;
    const key = FIELDS[s.field].key;
    if (k === 'up' || k === 'shift+tab') s.field = (s.field + FIELDS.length - 1) % FIELDS.length;
    else if (k === 'down' || k === 'tab') s.field = (s.field + 1) % FIELDS.length;
    else if (k === 'enter') rt.go('init-posture');
    else if (k === 'escape') rt.back();
    else if (k === 'backspace') initCtx.identity[key] = initCtx.identity[key].slice(0, -1);
    else if (k === 'space') initCtx.identity[key] += ' ';
    else if (k.length === 1) initCtx.identity[key] += k;
  },
};
