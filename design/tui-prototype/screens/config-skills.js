// screens/config-skills.js
//
// `netclaw config` -> Skill Sources. Unifies the two init steps (External Skills
// + Skill Feeds) into one inventory:
//   inventory (Local folders / Remote skill servers + add/rescan)
//     -> source detail (per-source actions)
//     -> add local:  path -> symlinks security -> name
//     -> add remote: URL (+ callout) -> probe -> [auth-required: reveal URL+token
//        form | unreachable: warning dialog] -> name
//
// The remote add uses the SAME probe-driven disclosure as the Search/SearXNG
// editor: a bearer token is requested only when the probe returns 401, on a
// combined URL+token form navigated with ↑/↓ or Tab. No explicit auth picker.

import { store, skillTotals } from '../mock/store.js';

const SYNC = ['15m', '1h', '6h', '24h'];
const check = (b) => (b ? '✓' : ' ');
const suggestName = (url) => (url || '').replace(/^https?:\/\//, '').split('.')[0] || 'remote-feed';

export const configSkills = {
  id: 'config-skills',
  state: {},

  init() {
    this.state = { screen: 'inventory', rowIndex: 0, detailIndex: 0, detailId: null, draft: '', token: '', authField: 1, pick: 0, probeStart: 0, dialogIndex: 0, cameFrom: '', nw: {} };
  },

  isAnimating() {
    return ['addLocalPath', 'addLocalName', 'addRemoteUrl', 'authForm', 'addRemoteName', 'rename', 'changeLocation', 'validating'].includes(this.state.screen);
  },

  flatRows() {
    const ss = store.skills.sources;
    return [
      ...ss.filter((s) => s.kind === 'local').map((src) => ({ kind: 'source', src })),
      ...ss.filter((s) => s.kind === 'remote').map((src) => ({ kind: 'source', src })),
      { kind: 'action', label: '+ Add local folder', act: 'addLocal' },
      { kind: 'action', label: '+ Add remote server', act: 'addRemote' },
      { kind: 'action', label: 'Rescan all sources', act: 'rescan' },
    ];
  },
  source() { return store.skills.sources.find((s) => s.id === this.state.detailId); },
  detailRows() {
    const s = this.source();
    const base = [{ label: 'Enabled', val: `[${check(s.enabled)}]`, act: 'toggle' }];
    if (s.kind === 'local') base.push({ label: 'Allow symlinks', val: `[${check(s.symlinks)}]`, act: 'symlinks' }, { label: 'Location', val: s.location, act: 'changeLocation' });
    else base.push({ label: 'URL', val: s.location, act: 'changeLocation' }, { label: 'Sync interval', val: `[◀ ${s.syncInterval.padEnd(4)} ▶]`, act: 'sync' });
    base.push({ label: 'Name', val: s.name, act: 'rename' });
    if (s.kind === 'remote' && s.hasToken) base.push({ label: 'Bearer token', val: '[Remove token]', act: 'removeToken' });
    base.push({ label: 'Rescan now', val: '', act: 'rescan' }, { label: 'Remove source', val: '[Remove]', act: 'remove' });
    return base;
  },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Skill Sources');
    ({
      inventory: this.rInventory, detail: this.rDetail,
      addLocalPath: this.rDraft, addLocalSymlinks: this.rChoice, addLocalName: this.rDraft,
      addRemoteUrl: this.rDraft, validating: this.rValidating, authForm: this.rAuthForm, dialog: this.rDialog, addRemoteName: this.rDraft,
      rename: this.rDraft, changeLocation: this.rDraft, removeConfirm: this.rChoice,
    }[this.state.screen]).call(this, scr, r, rt, W);
  },

  rInventory(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Skill Sources');
    W.helpLines(scr, r, r.y + 1, ['Places Netclaw loads skills from. Skill enablement stays in Security & Access.']);
    const rows = this.flatRows();
    let yy = r.y + 3; let header = null;
    rows.forEach((row, i) => {
      if (row.kind === 'source') {
        const h = row.src.kind === 'local' ? 'Local folders' : 'Remote skill servers';
        if (h !== header) { if (header) yy += 1; scr.text(r.x + 2, yy, h, { fg: 'fg', bold: true }); yy += 1; header = h; }
      } else if (header !== 'act') { yy += 1; header = 'act'; }
      const line = row.kind === 'source'
        ? `[${check(row.src.enabled)}] ${row.src.name.padEnd(16)} ${row.src.location.padEnd(26)} ${row.src.status}`
        : row.label;
      const focused = i === this.state.rowIndex;
      const dim = row.kind === 'source' && !row.src.enabled;
      if (focused) { scr.fillRect(r.x, yy, r.w, 1, ' ', { bg: 'accent', fg: 'onAccent' }); scr.text(r.x, yy, line, { bg: 'accent', fg: 'onAccent' }); }
      else scr.text(r.x, yy, line, { fg: dim ? 'faint' : 'text' });
      yy += 1;
    });
    const row = rows[this.state.rowIndex];
    W.helpLines(scr, r, yy + 1, [row?.kind === 'source' ? `${row.src.location} · ${row.src.skillCount} skills · ${row.src.enabled ? 'enabled' : 'disabled'}` : 'Add a source or rescan everything.']);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Open/Add  [Space] Toggle  [Bksp] Remove  [Esc] Settings Areas  [Ctrl+Q] Quit');
  },

  rDetail(scr, r, rt, W) {
    const s = this.source();
    W.heading(scr, r, r.y, s.name);
    W.line(scr, r, r.y + 1, `Type:   ${s.kind === 'local' ? 'Local folder' : 'Remote skill server'}`, 'fg');
    W.line(scr, r, r.y + 2, `Status: ${s.enabled ? s.status : 'Disabled'}`, s.enabled ? 'ok' : 'dim');
    const rows = this.detailRows();
    rows.forEach((row, i) => {
      const yy = r.y + 4 + i;
      const line = `${row.label.padEnd(18)} ${row.val}`;
      if (i === this.state.detailIndex) { scr.fillRect(r.x, yy, r.w, 1, ' ', { bg: 'accent', fg: 'onAccent' }); scr.text(r.x, yy, line, { bg: 'accent', fg: 'onAccent' }); }
      else scr.text(r.x, yy, line, { fg: 'text' });
    });
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [←/→] Sync  [Enter/Space] Activate  [Esc] Skill Sources  [Ctrl+Q] Quit');
  },

  rDraft(scr, r, rt, W) {
    const c = this.draftConfig();
    W.heading(scr, r, r.y, c.title);
    W.textInputPanel(scr, r, r.y + 2, c.label, this.state.draft, { password: c.password, placeholder: c.placeholder, focused: true, width: 56 });
    W.helpLines(scr, r, r.y + 6, [c.hint]);
    if (c.callout) {
      const inner = scr.box(r.x + 2, r.y + 8, 80, c.callout.lines.length + 2, { fg: 'warn' }, { border: 'rounded', title: c.callout.title, titleColor: 'warn' });
      c.callout.lines.forEach((l, i) => scr.text(inner.x + 1, inner.y + i, l, { fg: 'yellow' }));
    }
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[Type] Edit  [Enter] Apply  [Esc] Back  [Ctrl+Q] Quit');
  },

  rChoice(scr, r, rt, W) {
    const c = this.choiceConfig();
    W.heading(scr, r, r.y, c.title);
    W.helpLines(scr, r, r.y + 1, [c.hint]);
    W.selectionList(scr, r, r.y + 3, c.options, this.state.pick);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Back  [Ctrl+Q] Quit');
  },

  rValidating(scr, r, rt, W) {
    W.spinner(scr, r, r.y + 1, `Discovering skills at ${this.state.nw.url} ...`, 'accent');
    W.keyHints(scr, r, '[Ctrl+Q] Quit');
  },

  // Probe came back 401: reveal the URL + bearer token together (same pattern as
  // the SearXNG editor). The token field exists only because the probe demanded it.
  rAuthForm(scr, r, rt, W) {
    const s = this.state;
    W.heading(scr, r, r.y, 'This skill server requires a bearer token.');
    W.helpLines(scr, r, r.y + 1, [`Probed ${s.nw.url} → 401 Unauthorized. Add the server's bearer token, or fix the URL.`]);
    W.textInputPanel(scr, r, r.y + 3, 'Server URL', s.nw.url, { placeholder: 'https://skills.example.com', focused: s.authField === 0, width: 56 });
    W.textInputPanel(scr, r, r.y + 7, 'Bearer token', s.token, { password: true, placeholder: 'Enter the bearer token...', focused: s.authField === 1, width: 56 });
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓ or Tab] Move between fields  [Enter] Re-validate  [Esc] Back  [Ctrl+Q] Quit');
  },

  rDialog(scr, r, rt, W) {
    const auth = this.state.nw.reason === 'auth';
    const inner = scr.box(r.x + 2, r.y + 1, r.w - 4, 11, { fg: 'warn' }, { border: 'rounded', title: 'Skill Server Validation Warning', titleColor: 'warn' });
    scr.text(inner.x + 2, inner.y + 1, auth ? `Netclaw could not authenticate to ${this.state.nw.url}.` : `Netclaw could not reach ${this.state.nw.url}.`, { fg: 'text' });
    scr.text(inner.x + 2, inner.y + 3, auth ? '401 Unauthorized — this server requires a bearer token.' : 'No /.well-known/agent-skills/index.json was returned (connection refused).', { fg: 'yellow' });
    W.selectionList(scr, { x: inner.x + 2, y: inner.y, w: inner.w - 4, h: inner.h }, inner.y + 5, ['Retry validation', 'Back to edit', 'Save anyway'], this.state.dialogIndex, { barBg: 'warn', barFg: 'base' });
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Back to edit  [Ctrl+Q] Quit');
  },

  draftConfig() {
    const s = this.state;
    switch (s.screen) {
      case 'addLocalPath': return { title: 'Add a local skill folder.', label: 'Folder path', placeholder: '/path/to/team-skills', hint: 'This must be an existing local directory.' };
      case 'addLocalName': return { title: 'Review local folder source.', label: 'Source name', placeholder: 'team-skills', hint: 'Enter adds the source and autosaves.' };
      case 'addRemoteUrl': return { title: 'Add a remote skill server.', label: 'Server URL', placeholder: 'https://skills.example.com', hint: 'Netclaw probes /.well-known/agent-skills/index.json. You will be prompted for a token only if the server requires one.',
        callout: { title: 'What is a skill server?', lines: ['A skill server publishes agent skills over HTTP for a team or org.', 'Project: https://github.com/netclaw-dev/skill-server'] } };
      case 'addRemoteName': return { title: 'Review remote skill server source.', label: 'Source name', placeholder: 'acme-feed', hint: 'Enter adds the source and autosaves.' };
      case 'rename': return { title: 'Rename this skill source.', label: 'New name', placeholder: this.source().name, hint: 'Enter validates and autosaves the new name.' };
      case 'changeLocation': return { title: 'Change this source location.', label: this.source().kind === 'local' ? 'Folder path' : 'Server URL', placeholder: this.source().location, hint: 'Enter validates and autosaves the new path or URL.' };
      default: return { title: '', label: '', hint: '' };
    }
  },
  choiceConfig() {
    if (this.state.screen === 'addLocalSymlinks') return { title: 'Allow symlinks inside this folder?', hint: 'Symlinks can make a source scan files outside the folder.', options: ['No - stricter security', 'Yes - this folder intentionally uses symlinks'] };
    return { title: 'Remove this skill source from Netclaw config?', hint: 'This does not delete remote skills or local files.', options: ['Cancel', 'Remove source'] };
  },

  // ── transitions ──
  addSource(rt, src) {
    src.id = store.skills.nextId++;
    src.hasToken = !!this.state.token.trim() || !!src.hasToken;
    store.skills.sources.push(src);
    this.state.screen = 'inventory';
    rt.setStatus(`Added ${src.kind === 'local' ? 'local skill folder' : 'remote skill server'} ${src.name}. Saved.`, 'ok');
  },
  // Probe-driven disclosure, identical in spirit to the Search editor.
  startRemoteProbe(rt) {
    const s = this.state;
    s.cameFrom = s.screen;
    s.screen = 'validating'; s.probeStart = performance.now();
    rt.schedule(2000, () => {
      s.nw.skillCount = 27;
      const url = s.nw.url || '';
      const unreachable = /:99|\.invalid|unreach/i.test(url);
      const needsAuth = /acme|private|secure/i.test(url);
      const hasToken = !!s.token.trim();
      if (unreachable) { s.nw.reason = 'unreachable'; s.dialogIndex = 0; s.screen = 'dialog'; }
      else if (needsAuth && !hasToken) {
        s.nw.reason = 'auth';
        if (s.cameFrom === 'addRemoteUrl') { s.authField = 1; s.screen = 'authForm'; }  // first time: reveal token form
        else { s.dialogIndex = 0; s.screen = 'dialog'; }                                // skipped token -> warn
      } else { s.nw.hasToken = hasToken; s.nw.status = `${s.nw.skillCount} skills · just synced`; s.draft = suggestName(url); s.screen = 'addRemoteName'; }
    });
  },

  onKey(k, rt) {
    const s = this.state;
    const sc = s.screen;

    if (['addLocalPath', 'addLocalName', 'addRemoteUrl', 'addRemoteName', 'rename', 'changeLocation'].includes(sc)) {
      if (k === 'enter') return this.applyDraft(rt);
      if (k === 'escape') { s.screen = this.draftBack(); rt.setStatus(null); return; }
      if (k === 'backspace') s.draft = s.draft.slice(0, -1);
      else if (k === 'space') s.draft += ' ';
      else if (k.length === 1) s.draft += k;
      return;
    }
    if (['addLocalSymlinks', 'removeConfirm'].includes(sc)) {
      const opts = this.choiceConfig().options;
      if (k === 'up') s.pick = Math.max(0, s.pick - 1);
      else if (k === 'down') s.pick = Math.min(opts.length - 1, s.pick + 1);
      else if (k === 'enter') this.applyChoice(rt);
      else if (k === 'escape') { s.screen = this.choiceBack(); rt.setStatus(null); }
      return;
    }

    switch (sc) {
      case 'inventory': {
        const rows = this.flatRows(); const row = rows[s.rowIndex];
        if (k === 'up') s.rowIndex = Math.max(0, s.rowIndex - 1);
        else if (k === 'down') s.rowIndex = Math.min(rows.length - 1, s.rowIndex + 1);
        else if (k === 'space' && row.kind === 'source') { row.src.enabled = !row.src.enabled; rt.setStatus(`${row.src.name} ${row.src.enabled ? 'enabled' : 'disabled'}. Saved.`, 'ok'); }
        else if (k === 'backspace' && row.kind === 'source') { s.detailId = row.src.id; s.pick = 0; s.screen = 'removeConfirm'; }
        else if (k === 'enter') {
          if (row.kind === 'source') { s.detailId = row.src.id; s.detailIndex = 0; s.screen = 'detail'; }
          else if (row.act === 'addLocal') { s.nw = { kind: 'local', enabled: true, symlinks: false, skillCount: 0, status: 'pending scan' }; s.draft = ''; s.screen = 'addLocalPath'; }
          else if (row.act === 'addRemote') { s.nw = { kind: 'remote', enabled: true, hasToken: false, syncInterval: '1h', skillCount: 0 }; s.draft = ''; s.token = ''; s.screen = 'addRemoteUrl'; }
          else rt.setStatus('Rescanned all sources. 47 skills loaded.', 'ok');
        } else if (k === 'escape') rt.back();
        break;
      }
      case 'detail': {
        const rows = this.detailRows(); const row = rows[s.detailIndex]; const src = this.source();
        if (k === 'up') s.detailIndex = Math.max(0, s.detailIndex - 1);
        else if (k === 'down') s.detailIndex = Math.min(rows.length - 1, s.detailIndex + 1);
        else if ((k === 'left' || k === 'right') && row.act === 'sync') { const i = SYNC.indexOf(src.syncInterval); src.syncInterval = SYNC[(i + (k === 'right' ? 1 : -1) + SYNC.length) % SYNC.length]; rt.setStatus(`Sync interval set to ${src.syncInterval}. Saved.`, 'ok'); }
        else if (k === 'space' || k === 'enter') {
          if (row.act === 'toggle') { src.enabled = !src.enabled; rt.setStatus(`${src.name} ${src.enabled ? 'enabled' : 'disabled'}. Saved.`, 'ok'); }
          else if (row.act === 'symlinks') { src.symlinks = !src.symlinks; rt.setStatus(`Symlinks ${src.symlinks ? 'allowed' : 'blocked'}. Saved.`, 'ok'); }
          else if (row.act === 'sync') { const i = SYNC.indexOf(src.syncInterval); src.syncInterval = SYNC[(i + 1) % SYNC.length]; rt.setStatus(`Sync interval set to ${src.syncInterval}. Saved.`, 'ok'); }
          else if (row.act === 'changeLocation') { s.draft = src.location; s.screen = 'changeLocation'; }
          else if (row.act === 'rename') { s.draft = src.name; s.screen = 'rename'; }
          else if (row.act === 'removeToken') { src.hasToken = false; s.detailIndex = Math.max(0, s.detailIndex - 1); rt.setStatus('Bearer token removed. Saved.', 'ok'); }
          else if (row.act === 'rescan') rt.setStatus(`Rescanned ${src.name}.`, 'ok');
          else if (row.act === 'remove') { s.pick = 0; s.screen = 'removeConfirm'; }
        } else if (k === 'escape') { s.screen = 'inventory'; rt.setStatus(null); }
        break;
      }
      case 'validating':
        if (k === 'escape') { rt.clearTimers(); s.screen = s.cameFrom === 'authForm' ? 'authForm' : 'addRemoteUrl'; if (s.cameFrom !== 'authForm') s.draft = s.nw.url; }
        break;
      case 'authForm':
        if (k === 'up' || k === 'down' || k === 'tab' || k === 'shift+tab') s.authField = (s.authField + 1) % 2;
        else if (k === 'enter') { rt.setStatus(null); this.startRemoteProbe(rt); }
        else if (k === 'escape') { rt.clearTimers(); s.screen = 'addRemoteUrl'; s.draft = s.nw.url; }
        else if (s.authField === 0) { if (k === 'backspace') s.nw.url = s.nw.url.slice(0, -1); else if (k === 'space') s.nw.url += ' '; else if (k.length === 1) s.nw.url += k; }
        else { if (k === 'backspace') s.token = s.token.slice(0, -1); else if (k === 'space') s.token += ' '; else if (k.length === 1) s.token += k; }
        break;
      case 'dialog':
        if (k === 'up') s.dialogIndex = Math.max(0, s.dialogIndex - 1);
        else if (k === 'down') s.dialogIndex = Math.min(2, s.dialogIndex + 1);
        else if (k === 'enter') {
          if (s.dialogIndex === 0) this.startRemoteProbe(rt);                                  // Retry
          else if (s.dialogIndex === 1) { s.authField = s.nw.reason === 'auth' ? 1 : 0; s.screen = 'authForm'; }  // Back to edit (URL+token form)
          else { s.nw.hasToken = !!s.token.trim(); s.nw.status = `added (probe failed) · ${s.nw.skillCount} skills`; s.draft = suggestName(s.nw.url); s.screen = 'addRemoteName'; }  // Save anyway -> name it
        } else if (k === 'escape') { s.authField = 1; s.screen = 'authForm'; }
        break;
    }
  },

  applyDraft(rt) {
    const s = this.state;
    switch (s.screen) {
      case 'addLocalPath': if (!s.draft.trim()) return; s.nw.location = s.draft.trim(); s.draft = (s.draft.split('/').pop() || 'local-skills'); s.screen = 'addLocalSymlinks'; s.pick = 0; break;
      case 'addLocalName': s.nw.name = s.draft.trim() || 'local-skills'; s.nw.status = 'pending scan'; this.addSource(rt, s.nw); break;
      case 'addRemoteUrl': if (!s.draft.trim()) return; s.nw.url = s.draft.trim(); s.nw.location = s.draft.trim(); this.startRemoteProbe(rt); break;
      case 'addRemoteName': s.nw.name = s.draft.trim() || 'remote-feed'; s.nw.location = s.nw.url; this.addSource(rt, s.nw); break;
      case 'rename': { const src = this.source(); src.name = s.draft.trim() || src.name; s.screen = 'detail'; rt.setStatus(`Renamed to ${src.name}. Saved.`, 'ok'); break; }
      case 'changeLocation': { const src = this.source(); src.location = s.draft.trim() || src.location; s.screen = 'detail'; rt.setStatus('Location updated. Saved.', 'ok'); break; }
    }
  },
  draftBack() {
    return { addLocalPath: 'inventory', addLocalName: 'addLocalSymlinks', addRemoteUrl: 'inventory', addRemoteName: 'addRemoteUrl', rename: 'detail', changeLocation: 'detail' }[this.state.screen];
  },
  applyChoice(rt) {
    const s = this.state;
    if (s.screen === 'addLocalSymlinks') { s.nw.symlinks = s.pick === 1; s.draft = s.draft || 'local-skills'; s.screen = 'addLocalName'; }
    else {
      if (s.pick === 1) { store.skills.sources = store.skills.sources.filter((x) => x.id !== s.detailId); s.screen = 'inventory'; s.rowIndex = 0; rt.setStatus('Skill source removed. Saved.', 'ok'); }
      else { s.screen = this.source() ? 'detail' : 'inventory'; rt.setStatus(null); }
    }
  },
  choiceBack() {
    return { addLocalSymlinks: 'addLocalPath', removeConfirm: (this.source() ? 'detail' : 'inventory') }[this.state.screen];
  },
};
