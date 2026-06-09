// app.js — prototype runtime: screen registry, key router, nav stack, timers,
// animation tick, auto-fit.
//
// The tick loop is what makes spinners animate and the cursor blink: when the
// active screen reports isAnimating(), the runtime re-renders at the spinner
// cadence. One-shot rt.schedule() timers drive scripted transitions (probe
// completes, OAuth succeeds) and re-render on fire. This mirrors how Termina's
// SpinnerNode self-animates + how the wizard auto-advances on probe success.

import { Screen, SEM } from './engine/screen.js';
import * as W from './engine/widgets.js';
import { providerPicker } from './screens/init-provider.js';
import { securityPosture } from './screens/init-posture.js';
import { initIdentity } from './screens/init-identity.js';
import { initFeatures } from './screens/init-features.js';
import { initHealth } from './screens/init-health.js';
import { initExisting } from './screens/init-existing.js';
import { initReset } from './screens/init-reset.js';
import { configDashboard } from './screens/config-dashboard.js';
import { configSecurity } from './screens/config-security.js';
import { configSearch } from './screens/config-search.js';
import { configExposure } from './screens/config-exposure.js';
import { configChannels } from './screens/config-channels.js';
import { configSkills } from './screens/config-skills.js';
import { configInbound, configBrowser, configTelemetry, configWorkspaces } from './screens/config-rows.js';
import { configWebhooks } from './screens/config-webhooks.js';

const TICK_MS = 80; // spinner frame cadence

const rt = {
  term: document.getElementById('term'),
  scr: new Screen(),
  screens: new Map(),
  order: [],
  current: null,
  stack: [],
  status: null,
  _timers: new Set(),
  _tick: null,

  register(screen) { this.screens.set(screen.id, screen); this.order.push(screen.id); },

  go(id, opts = {}) { if (this.current) this.stack.push(this.current); this._activate(id, opts.reset !== false); },
  replace(id, opts = {}) { this._activate(id, opts.reset !== false); },
  back() { const prev = this.stack.pop(); if (prev) this._activate(prev, false); },

  _activate(id, reset) {
    this.clearTimers();
    this.current = id;
    const s = this.screens.get(id);
    if (reset && s.init) s.init(this);
    this.status = null;
    syncSelect();
    this.render();
  },

  setStatus(text, color = SEM.ok) { this.status = text ? { text, color } : null; },

  // One-shot timer that re-renders when it fires. Tracked so navigation can cancel.
  schedule(ms, fn) {
    const id = setTimeout(() => { this._timers.delete(id); fn(); this.render(); }, ms);
    this._timers.add(id);
    return id;
  },
  clearTimers() { this._timers.forEach(clearTimeout); this._timers.clear(); },

  startTick() { if (!this._tick) this._tick = setInterval(() => this.render(), TICK_MS); },
  stopTick() { if (this._tick) { clearInterval(this._tick); this._tick = null; } },

  render() {
    this.scr.clear();
    const s = this.screens.get(this.current);
    s.render(this.scr, this, W);
    this.scr.render(this.term);
    // Start/stop the animation loop based on the active screen's needs.
    (s.isAnimating && s.isAnimating(this)) ? this.startTick() : this.stopTick();
    fitToWidth();
  },
};

// ---- key normalization ----
function normKey(e) {
  switch (e.key) {
    case 'ArrowUp': return 'up';
    case 'ArrowDown': return 'down';
    case 'ArrowLeft': return 'left';
    case 'ArrowRight': return 'right';
    case 'Enter': return 'enter';
    case 'Escape': return 'escape';
    case ' ': return 'space';
    case 'Tab': return e.shiftKey ? 'shift+tab' : 'tab';
    case 'Backspace': return 'backspace';
    default: return e.key.length === 1 ? e.key : null;
  }
}
const NAV_KEYS = new Set(['up', 'down', 'left', 'right', 'enter', 'space', 'tab', 'shift+tab', 'escape']);

rt.term.addEventListener('keydown', (e) => {
  const k = normKey(e);
  if (!k) return;
  if (NAV_KEYS.has(k)) e.preventDefault();
  const s = rt.screens.get(rt.current);
  if (s.onKey) s.onKey(k, rt);
  rt.render();
});

// ---- auto-fit: scale the terminal to the viewport width ----
function fitToWidth() {
  const fit = document.getElementById('fit-toggle').checked;
  rt.term.style.transform = 'scale(1)';
  const stage = rt.term.parentElement;
  if (!fit) { stage.style.height = ''; return; }
  const avail = stage.clientWidth - 44;
  const scale = Math.min(1, avail / rt.term.scrollWidth);
  rt.term.style.transform = `scale(${scale})`;
  stage.style.height = (rt.term.scrollHeight * scale + 44) + 'px';
}
window.addEventListener('resize', fitToWidth);
document.getElementById('fit-toggle').addEventListener('change', () => rt.render());

// ---- dev screen switcher ----
const select = document.getElementById('screen-select');
function syncSelect() { if (select.value !== rt.current) select.value = rt.current; }
select.addEventListener('change', () => rt.replace(select.value));

// Measure the 14px text advance so box-drawing cells (--cell-w wide) line up
// exactly with the text grid. Re-measure once the webfont loads.
function measureCell() {
  const probe = document.createElement('span');
  probe.style.cssText = 'position:absolute;visibility:hidden;white-space:pre;font:14px/16px inherit;';
  probe.style.fontFamily = getComputedStyle(rt.term).fontFamily;
  probe.textContent = '0'.repeat(100);
  rt.term.appendChild(probe);
  const w = probe.getBoundingClientRect().width / 100;
  probe.remove();
  if (w > 0) document.documentElement.style.setProperty('--cell-w', w + 'px');
}

// ---- boot ----
[configDashboard, configSecurity, configSearch, configExposure, configChannels, configSkills,
  configInbound, configBrowser, configTelemetry, configWorkspaces, configWebhooks,
  providerPicker, initIdentity, securityPosture, initFeatures, initHealth, initExisting, initReset]
  .forEach((s) => rt.register(s));
rt.order.forEach((id) => {
  const o = document.createElement('option');
  o.value = id; o.textContent = id;
  select.appendChild(o);
});
measureCell();
rt.replace('config-dashboard');
rt.term.focus();
if (document.fonts && document.fonts.ready) {
  document.fonts.ready.then(() => { measureCell(); rt.render(); });
}
