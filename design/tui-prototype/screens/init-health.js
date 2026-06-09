// screens/init-health.js — simplified init, Step 5 of 5: Health Check / post-flight.
// Runs end-to-end checks behind a spinner, shows the summary, and nudges the
// operator toward `netclaw chat` and `netclaw config` (TUI-003 Init.5).

import { initCtx } from '../mock/initctx.js';

export const initHealth = {
  id: 'init-health',
  state: { phase: 'prompt', start: 0 },
  init() { this.state = { phase: 'prompt', start: 0 }; },
  isAnimating() { return this.state.phase === 'running' || this.state.phase === 'launched'; },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Netclaw Setup');
    W.stepIndicator(scr, r, { step: 5, total: 5, title: 'Health Check', pct: 100 });
    const s = this.state;

    if (s.phase === 'prompt') {
      W.heading(scr, r, r.y + 2, 'Final checks before launch.');
      W.helpLines(scr, r, r.y + 4, ['Press Enter to run health checks and finish setup.']);
      W.keyHints(scr, r, '[Enter] Run checks  [Esc] Back  [Ctrl+Q] Quit');
    } else if (s.phase === 'running') {
      W.spinner(scr, r, r.y + 2, 'Running health checks...', 'warn', Math.floor((performance.now() - s.start) / 1000));
      W.helpLines(scr, r, r.y + 4, ['Validating provider, model, identity, and config write.']);
      W.keyHints(scr, r, '[Ctrl+Q] Quit');
    } else if (s.phase === 'launched') {
      W.spinner(scr, r, r.y + 2, 'Launching netclaw chat...', 'accent');
      W.keyHints(scr, r, '[Ctrl+Q] Quit');
    } else {
      W.heading(scr, r, r.y + 2, 'Netclaw is ready.');
      const checks = [
        `LLM provider configured (${initCtx.provider})`,
        `Model selected (${initCtx.model})`,
        `Identity written (agent: ${initCtx.identity.agentName})`,
        `Security posture: ${initCtx.posture}`,
        'Config written to ~/.netclaw/config/netclaw.json',
      ];
      checks.forEach((c, i) => W.line(scr, r, r.y + 4 + i, `✓ ${c}`, 'ok'));
      W.helpLines(scr, r, r.y + 10, [
        'Next steps:',
        '  netclaw chat    — start talking to your agent',
        '  netclaw config  — adjust settings any time',
      ]);
      W.keyHints(scr, r, '[Enter] Launch netclaw chat  [Esc] Back  [Ctrl+Q] Quit');
    }
  },

  onKey(k, rt) {
    const s = this.state;
    if (s.phase === 'prompt') {
      if (k === 'enter') { s.phase = 'running'; s.start = performance.now(); rt.schedule(2600, () => { s.phase = 'done'; }); }
      else if (k === 'escape') rt.back();
    } else if (s.phase === 'done') {
      if (k === 'enter') { s.phase = 'launched'; }
      else if (k === 'escape') rt.back();
    }
  },
};
