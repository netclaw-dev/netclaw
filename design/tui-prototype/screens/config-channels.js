// screens/config-channels.js
//
// `netclaw config` -> Channels. The most multi-step editor in the suite, mirroring
// ChannelsConfigPage's screen machine. Two entry paths from the adapter picker:
//   - configured adapter -> management menu (channels & permissions, allowed
//     users, DMs, rotate credentials, reset)
//   - UNconfigured adapter -> first-time setup: credentials (adapter-specific) ->
//     probe -> first channel -> lands in that adapter's management menu
//
// Since the simplified `netclaw init` defers channels to config, first-time setup
// lives here as a config-native linear flow. The active adapter is generalized so
// every screen works for Slack / Discord / Mattermost. Bespoke by design.

import { store } from '../mock/store.js';

const ADAPTERS = ['Slack', 'Discord', 'Mattermost'];
const AUDIENCES = ['Personal', 'Team', 'Public'];
const AUD_DESC = {
  Personal: 'Private operator or owner-only context.',
  Team: 'Trusted internal channel.',
  Public: 'Untrusted or broad audience with strict controls.',
};

const MENU = [
  ['Channels & permissions', 'Add, remove, and set audience per channel.', 'channels'],
  ['Allowed users', 'Restrict who can interact with the bot.', 'allowedUsers'],
  ['Direct messages', 'Allow or restrict DMs and their audience.', 'dms'],
  ['Rotate credentials', 'Update tokens without re-setup.', 'credentials'],
  ['Reset connection', 'Remove all settings for this adapter.', 'resetConfirm'],
  ['Done', 'Back to the channel list.', 'picker'],
];

// Credential fields per adapter. Slack = Socket Mode (bot + app tokens);
// Discord = bot token; Mattermost = self-hosted server URL + bot token.
const CRED_FIELDS = {
  Slack: [{ key: 'bot', label: 'Bot token', placeholder: 'xoxb-...', secret: true }, { key: 'app', label: 'App token', placeholder: 'xapp-... (Socket Mode)', secret: true }],
  Discord: [{ key: 'bot', label: 'Bot token', placeholder: 'Discord bot token', secret: true }],
  Mattermost: [{ key: 'url', label: 'Server URL', placeholder: 'https://mattermost.example.com', secret: false }, { key: 'bot', label: 'Bot token', placeholder: 'Mattermost bot token', secret: true }],
};
const SETUP_HINT = {
  Slack: 'Create a Slack app with Socket Mode, then paste its bot and app tokens.',
  Discord: 'Create a Discord application + bot, then paste the bot token.',
  Mattermost: 'Point at your Mattermost server and paste a bot account token.',
};

const check = (b) => (b ? '✓' : ' ');
const cyc = (v) => `[◀ ${v.padEnd(8)} ▶]`;

export const configChannels = {
  id: 'config-channels',
  state: {},

  init() {
    this.state = {
      screen: 'picker', adapter: 'Slack', pickerIndex: 0, menuIndex: 0,
      channelIndex: 0, audienceIndex: 0, editingIdx: 0,
      addInput: '', addAudIndex: 1, usersDraft: '', dmsRow: 0,
      credIndex: 0, credDrafts: {}, resetIndex: 0,
      setupField: 0, setupDrafts: {}, setupChannel: '', setupAud: 1, probeStart: 0, dialogIndex: 0,
    };
  },

  isAnimating() { return ['addChannel', 'resolveChannel', 'allowedUsers', 'credentials', 'setupCreds', 'setupChannel', 'setupValidating'].includes(this.state.screen); },

  active() { return store.channels[this.state.adapter]; },
  credFields() { return CRED_FIELDS[this.state.adapter]; },

  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Channels');
    ({
      picker: this.rPicker, menu: this.rMenu, channels: this.rChannels, editAudience: this.rEditAud,
      addChannel: this.rAddChannel, resolveChannel: this.rResolveChannel, allowedUsers: this.rUsers, dms: this.rDms,
      credentials: this.rCreds, resetConfirm: this.rReset,
      setupCreds: this.rSetupCreds, setupValidating: this.rSetupValidating, setupDialog: this.rSetupDialog, setupChannel: this.rSetupChannel,
    }[this.state.screen]).call(this, scr, r, rt, W);
  },

  rPicker(scr, r, rt, W) {
    W.heading(scr, r, r.y, 'Which channels would you like to connect?');
    const rows = ADAPTERS.map((a) => {
      const cfg = store.channels[a];
      const sum = cfg.configured ? `configured · ${cfg.channels.length} channels` : '(not configured)';
      return `[${cfg.configured ? 'x' : ' '}] ${a.padEnd(12)} ${sum}`;
    });
    const after = W.selectionList(scr, r, r.y + 2, rows, this.state.pickerIndex);
    W.helpLines(scr, r, after + 1, ['Enter a configured adapter to manage it; an unconfigured one starts first-time setup.']);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Manage/Connect  [Esc] Settings Areas  [Ctrl+Q] Quit');
  },

  rMenu(scr, r, rt, W) {
    const a = this.state.adapter; const s = this.active();
    W.heading(scr, r, r.y, `${a} is configured.`);
    W.helpLines(scr, r, r.y + 1, [`${s.channels.length} channels · DMs ${s.dms.enabled ? 'on' : 'off'}`]);
    W.line(scr, r, r.y + 3, 'What would you like to do?', 'fg');
    const after = W.selectionList(scr, r, r.y + 5, MENU.map(([l]) => l), this.state.menuIndex);
    W.helpLines(scr, r, after + 1, [MENU[this.state.menuIndex][1]]);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Channels  [Ctrl+Q] Quit');
  },

  channelRows() { return [...this.active().channels.map((c) => ({ kind: 'channel', c })), { kind: 'add' }, { kind: 'done' }]; },
  rChannels(scr, r, rt, W) {
    W.heading(scr, r, r.y, `${this.state.adapter} > Channels & Permissions`);
    W.helpLines(scr, r, r.y + 1, ['Configure allowed channels and their audience/trust level.']);
    const rows = this.channelRows();
    let yy = r.y + 3;
    if (this.active().channels.length === 0) { scr.text(r.x + 2, yy, 'No channels yet — add one below.', { fg: 'dim' }); yy += 1; }
    rows.forEach((row, i) => {
      const focused = i === this.state.channelIndex;
      const line = row.kind === 'channel' ? `${row.c.name.padEnd(20)} ${cyc(row.c.audience)}` : row.kind === 'add' ? '+ Add channel' : 'Done';
      if (focused) { scr.fillRect(r.x, yy, r.w, 1, ' ', { bg: 'accent', fg: 'onAccent' }); scr.text(r.x, yy, line, { bg: 'accent', fg: 'onAccent' }); }
      else scr.text(r.x, yy, line, { fg: 'text' });
      yy += 1;
    });
    W.helpLines(scr, r, yy + 1, ['Audience controls which tools and data this channel can use.']);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [←/→] Audience  [Enter] Edit/Done  [a] Add  [Del] Remove  [Esc] Menu');
  },

  rEditAud(scr, r, rt, W) {
    const c = this.active().channels[this.state.editingIdx];
    W.heading(scr, r, r.y, `${this.state.adapter} > ${c.name}`);
    W.helpLines(scr, r, r.y + 1, [`Channel ID: ${c.id}`]);
    W.line(scr, r, r.y + 3, 'Who is this channel for?', 'fg');
    W.selectionList(scr, r, r.y + 5, AUDIENCES.map((a) => `${a.padEnd(10)} ${AUD_DESC[a]}`), this.state.audienceIndex);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Apply  [Esc] Channels  [Ctrl+Q] Quit');
  },

  rAddChannel(scr, r, rt, W) {
    W.heading(scr, r, r.y, `${this.state.adapter} > Add Channel`);
    W.line(scr, r, r.y + 2, 'Channel name or ID:', 'fg');
    W.textInputPanel(scr, r, r.y + 3, 'Channel', this.state.addInput, { placeholder: 'channel ID or #name', focused: true, width: 40 });
    W.helpLines(scr, r, r.y + 7, [
      `Netclaw resolves the channel on ${this.state.adapter} and adds it at the ${store.posture} default audience.`,
      'Change its audience afterward with ←/→ on the channel list.',
    ]);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[Type] Channel  [Enter] Resolve & add  [Esc] Channels  [Ctrl+Q] Quit');
  },

  rResolveChannel(scr, r, rt, W) {
    W.spinner(scr, r, r.y + 2, `Resolving ${this.state.addInput.trim()} on ${this.state.adapter}...`, 'warn');
    W.helpLines(scr, r, r.y + 4, ['Verifying the channel exists and the bot can access it.']);
    W.keyHints(scr, r, '[Ctrl+Q] Quit');
  },

  rUsers(scr, r, rt, W) {
    W.heading(scr, r, r.y, `${this.state.adapter} > Allowed Users`);
    W.helpLines(scr, r, r.y + 1, ['Leave blank to allow anyone in allowed channels.']);
    W.line(scr, r, r.y + 3, 'User IDs:', 'fg');
    W.textInputPanel(scr, r, r.y + 4, 'User IDs', this.state.usersDraft, { placeholder: 'U123, U456', focused: true, width: 50 });
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[Type] Edit  [Enter] Apply  [Esc] Menu  [Ctrl+Q] Quit');
  },

  rDms(scr, r, rt, W) {
    const dms = this.active().dms;
    W.heading(scr, r, r.y, `${this.state.adapter} > Direct Messages`);
    W.helpLines(scr, r, r.y + 1, ['Enable DMs only for audiences you trust.']);
    W.selectionList(scr, r, r.y + 3, [`[${check(dms.enabled)}] Allow direct messages`, `DM audience      ${cyc(dms.audience)}`], this.state.dmsRow);
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Space] Toggle  [←/→] Audience  [Enter] Apply  [Esc] Menu');
  },

  rCreds(scr, r, rt, W) {
    W.heading(scr, r, r.y, `${this.state.adapter} > Credentials`);
    W.helpLines(scr, r, r.y + 1, ['Secret fields are blank by design. Leave blank to keep existing secrets.']);
    let yy = r.y + 3;
    this.credFields().forEach((f, i) => {
      const focused = i === this.state.credIndex;
      scr.text(r.x + 2, yy, `${f.label}:`, { fg: focused ? 'accent' : 'text' });
      W.textInputPanel(scr, r, yy + 1, f.label, this.state.credDrafts[f.key] || '', { password: f.secret, placeholder: f.placeholder, focused, width: 46 });
      yy += 4;
    });
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[Tab] Next field  [Type] Edit  [Enter] Apply  [Esc] Menu  [Ctrl+Q] Quit');
  },

  rReset(scr, r, rt, W) {
    const a = this.state.adapter;
    W.heading(scr, r, r.y, `Reset ${a} connection?`);
    W.helpLines(scr, r, r.y + 1, [`This removes ${a} credentials, allowed channels, allowed users,`, 'DM settings, and channel permission mappings immediately.']);
    W.selectionList(scr, r, r.y + 4, ['Cancel', `Yes, reset ${a}`], this.state.resetIndex, this.state.resetIndex === 1 ? { barBg: 'err', barFg: 'base' } : {});
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Menu  [Ctrl+Q] Quit');
  },

  // ── first-time setup ──
  rSetupCreds(scr, r, rt, W) {
    const a = this.state.adapter;
    W.heading(scr, r, r.y, `Connect ${a}`);
    W.helpLines(scr, r, r.y + 1, [SETUP_HINT[a]]);
    let yy = r.y + 3;
    this.credFields().forEach((f, i) => {
      const focused = i === this.state.setupField;
      scr.text(r.x + 2, yy, `${f.label}:`, { fg: focused ? 'accent' : 'text' });
      W.textInputPanel(scr, r, yy + 1, f.label, this.state.setupDrafts[f.key] || '', { password: f.secret, placeholder: f.placeholder, focused, width: 46 });
      yy += 4;
    });
    if (rt.status) W.statusLine(scr, r, rt.status.text, rt.status.color);
    W.keyHints(scr, r, '[↑/↓ or Tab] Fields  [Type] Edit  [Enter] Connect  [Esc] Channels  [Ctrl+Q] Quit');
  },

  rSetupValidating(scr, r, rt, W) {
    W.spinner(scr, r, r.y + 2, `Connecting to ${this.state.adapter}...`, 'warn', Math.floor((performance.now() - this.state.probeStart) / 1000));
    W.helpLines(scr, r, r.y + 4, ['Validating tokens and opening the connection.']);
    W.keyHints(scr, r, '[Ctrl+Q] Quit');
  },

  rSetupDialog(scr, r, rt, W) {
    const inner = scr.box(r.x + 2, r.y + 1, r.w - 4, 11, { fg: 'warn' }, { border: 'rounded', title: `${this.state.adapter} Connection Warning`, titleColor: 'warn' });
    scr.text(inner.x + 2, inner.y + 1, `Netclaw could not authenticate to ${this.state.adapter}.`, { fg: 'text' });
    scr.text(inner.x + 2, inner.y + 3, 'The tokens were rejected (401). Check them and try again.', { fg: 'yellow' });
    W.selectionList(scr, { x: inner.x + 2, y: inner.y, w: inner.w - 4, h: inner.h }, inner.y + 5, ['Retry validation', 'Back to edit', 'Save anyway'], this.state.dialogIndex, { barBg: 'warn', barFg: 'base' });
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Back to edit  [Ctrl+Q] Quit');
  },

  rSetupChannel(scr, r, rt, W) {
    W.heading(scr, r, r.y, `${this.state.adapter} > First channel`);
    W.helpLines(scr, r, r.y + 1, [
      `Add a channel now, or leave blank to add channels later.`,
      `It's added at the ${store.posture} default audience — change it with ←/→ on the channel list.`,
    ]);
    W.line(scr, r, r.y + 4, 'Channel name or ID:', 'fg');
    W.textInputPanel(scr, r, r.y + 5, 'Channel', this.state.setupChannel, { placeholder: 'channel ID or #name (optional)', focused: true, width: 40 });
    W.keyHints(scr, r, '[Type] Channel  [Enter] Finish  [Esc] Back  [Ctrl+Q] Quit');
  },

  // ── transitions ──
  // Resolve the channel against the adapter before adding it (does it exist? can
  // the bot see it?). On success it's added at the system-default audience; the
  // operator then tunes it with ←/→ on the channel list.
  startResolve(rt) {
    const s = this.state;
    s.screen = 'resolveChannel'; s.probeStart = performance.now();
    rt.schedule(1500, () => {
      const raw = s.addInput.trim();
      if (/notfound|missing|nope|xxx/i.test(raw)) {
        rt.setStatus(`Channel ${raw} not found on ${s.adapter}. Check the name, or invite the bot to it.`, 'err');
        s.screen = 'addChannel';
        return;
      }
      const name = raw.startsWith('#') || raw.startsWith('C') ? raw : `#${raw}`;
      this.active().channels.push({ id: raw.startsWith('C') ? raw : `C${Math.abs(raw.length * 7919) % 99999}`, name, audience: store.posture });
      s.channelIndex = this.active().channels.length - 1; // focus the new row
      s.screen = 'channels';
      rt.setStatus(`Added ${name} at the ${store.posture} default. Use ←/→ to change its audience.`, 'ok');
    });
  },
  startSetupProbe(rt) {
    const s = this.state;
    s.screen = 'setupValidating'; s.probeStart = performance.now();
    rt.schedule(2200, () => {
      const bad = /bad|wrong|invalid/i.test(s.setupDrafts.bot || '');
      if (bad) { s.dialogIndex = 0; s.screen = 'setupDialog'; }
      else { s.setupChannel = ''; s.setupAud = 1; s.screen = 'setupChannel'; }
    });
  },
  finishSetup(rt) {
    const s = this.state; const a = s.adapter; const raw = s.setupChannel.trim();
    const channels = raw
      ? [{ id: raw.startsWith('C') ? raw : `C${Math.abs(raw.length * 7919) % 99999}`, name: raw.startsWith('#') || raw.startsWith('C') ? raw : `#${raw}`, audience: store.posture }]
      : [];
    store.channels[a] = { configured: true, channels, users: '', dms: { enabled: false, audience: 'Personal' } };
    s.screen = 'menu'; s.menuIndex = 0;
    rt.setStatus(`${a} connected${raw ? ` · added ${channels[0].name} (${store.posture} default)` : ''}. Saved.`, 'ok');
  },

  onKey(k, rt) {
    const s = this.state;
    switch (s.screen) {
      case 'picker':
        if (k === 'up') s.pickerIndex = Math.max(0, s.pickerIndex - 1);
        else if (k === 'down') s.pickerIndex = Math.min(ADAPTERS.length - 1, s.pickerIndex + 1);
        else if (k === 'enter') {
          s.adapter = ADAPTERS[s.pickerIndex]; rt.setStatus(null);
          if (this.active().configured) { s.screen = 'menu'; s.menuIndex = 0; }
          else { s.setupDrafts = {}; s.setupField = 0; s.screen = 'setupCreds'; }
        } else if (k === 'escape') rt.back();
        break;

      case 'menu':
        if (k === 'up') s.menuIndex = Math.max(0, s.menuIndex - 1);
        else if (k === 'down') s.menuIndex = Math.min(MENU.length - 1, s.menuIndex + 1);
        else if (k === 'enter') {
          const t = MENU[s.menuIndex][2];
          if (t === 'picker') s.screen = 'picker';
          else if (t === 'channels') { s.screen = 'channels'; s.channelIndex = 0; }
          else if (t === 'allowedUsers') { s.screen = 'allowedUsers'; s.usersDraft = this.active().users; }
          else if (t === 'dms') { s.screen = 'dms'; s.dmsRow = 0; }
          else if (t === 'credentials') { s.screen = 'credentials'; s.credIndex = 0; s.credDrafts = {}; }
          else if (t === 'resetConfirm') { s.screen = 'resetConfirm'; s.resetIndex = 0; }
          rt.setStatus(null);
        } else if (k === 'escape') { s.screen = 'picker'; rt.setStatus(null); }
        break;

      case 'channels': {
        const rows = this.channelRows(); const row = rows[s.channelIndex];
        if (k === 'up') s.channelIndex = Math.max(0, s.channelIndex - 1);
        else if (k === 'down') s.channelIndex = Math.min(rows.length - 1, s.channelIndex + 1);
        else if ((k === 'left' || k === 'right') && row.kind === 'channel') { const i = AUDIENCES.indexOf(row.c.audience); row.c.audience = AUDIENCES[(i + (k === 'right' ? 1 : -1) + 3) % 3]; rt.setStatus(`${row.c.name} audience set to ${row.c.audience}. Saved.`, 'ok'); }
        else if (k === 'a') { s.screen = 'addChannel'; s.addInput = ''; s.addAudIndex = 1; rt.setStatus(null); }
        else if (k === 'backspace' && row.kind === 'channel') { const name = row.c.name; this.active().channels.splice(s.channelIndex, 1); s.channelIndex = Math.max(0, s.channelIndex - 1); rt.setStatus(`Removed ${name}. Saved.`, 'ok'); }
        else if (k === 'enter') {
          if (row.kind === 'channel') { s.editingIdx = s.channelIndex; s.audienceIndex = AUDIENCES.indexOf(row.c.audience); s.screen = 'editAudience'; rt.setStatus(null); }
          else if (row.kind === 'add') { s.screen = 'addChannel'; s.addInput = ''; s.addAudIndex = 1; rt.setStatus(null); }
          else { s.screen = 'menu'; rt.setStatus(null); }
        } else if (k === 'escape') { s.screen = 'menu'; rt.setStatus(null); }
        break;
      }

      case 'editAudience':
        if (k === 'up') s.audienceIndex = Math.max(0, s.audienceIndex - 1);
        else if (k === 'down') s.audienceIndex = Math.min(AUDIENCES.length - 1, s.audienceIndex + 1);
        else if (k === 'enter') { const c = this.active().channels[s.editingIdx]; c.audience = AUDIENCES[s.audienceIndex]; s.screen = 'channels'; rt.setStatus(`${c.name} audience set to ${c.audience}. Saved.`, 'ok'); }
        else if (k === 'escape') { s.screen = 'channels'; rt.setStatus(null); }
        break;

      case 'addChannel':
        if (k === 'enter') { if (s.addInput.trim()) this.startResolve(rt); else { s.screen = 'channels'; rt.setStatus(null); } }
        else if (k === 'escape') { s.screen = 'channels'; rt.setStatus(null); }
        else if (k === 'backspace') s.addInput = s.addInput.slice(0, -1);
        else if (k === 'space') s.addInput += ' ';
        else if (k.length === 1) s.addInput += k;
        break;

      case 'resolveChannel':
        if (k === 'escape') { rt.clearTimers(); s.screen = 'addChannel'; }
        break;

      case 'allowedUsers':
        if (k === 'enter') { this.active().users = s.usersDraft.trim(); rt.setStatus('Allowed users saved.', 'ok'); s.screen = 'menu'; }
        else if (k === 'escape') { s.screen = 'menu'; rt.setStatus(null); }
        else if (k === 'backspace') s.usersDraft = s.usersDraft.slice(0, -1);
        else if (k === 'space') s.usersDraft += ' ';
        else if (k.length === 1) s.usersDraft += k;
        break;

      case 'dms': {
        const dms = this.active().dms;
        if (k === 'up') s.dmsRow = Math.max(0, s.dmsRow - 1);
        else if (k === 'down') s.dmsRow = Math.min(1, s.dmsRow + 1);
        else if (k === 'space' && s.dmsRow === 0) { dms.enabled = !dms.enabled; rt.setStatus(`Direct messages ${dms.enabled ? 'enabled' : 'disabled'}. Saved.`, 'ok'); }
        else if ((k === 'left' || k === 'right') && s.dmsRow === 1) { const i = AUDIENCES.indexOf(dms.audience); dms.audience = AUDIENCES[(i + (k === 'right' ? 1 : -1) + 3) % 3]; rt.setStatus(`DM audience set to ${dms.audience}. Saved.`, 'ok'); }
        else if (k === 'enter') { s.screen = 'menu'; rt.setStatus('Direct message settings saved.', 'ok'); }
        else if (k === 'escape') { s.screen = 'menu'; rt.setStatus(null); }
        break;
      }

      case 'credentials': {
        const fields = this.credFields();
        if (k === 'tab' || k === 'down') s.credIndex = (s.credIndex + 1) % fields.length;
        else if (k === 'shift+tab' || k === 'up') s.credIndex = (s.credIndex + fields.length - 1) % fields.length;
        else if (k === 'enter') { s.screen = 'menu'; rt.setStatus('Credentials updated. Saved.', 'ok'); }
        else if (k === 'escape') { s.screen = 'menu'; rt.setStatus(null); }
        else { const key = fields[s.credIndex].key; if (k === 'backspace') s.credDrafts[key] = (s.credDrafts[key] || '').slice(0, -1); else if (k.length === 1) s.credDrafts[key] = (s.credDrafts[key] || '') + k; }
        break;
      }

      case 'resetConfirm':
        if (k === 'up') s.resetIndex = Math.max(0, s.resetIndex - 1);
        else if (k === 'down') s.resetIndex = Math.min(1, s.resetIndex + 1);
        else if (k === 'enter') {
          if (s.resetIndex === 1) { store.channels[s.adapter] = { configured: false }; rt.setStatus(`${s.adapter} connection reset.`, 'ok'); s.screen = 'picker'; }
          else { s.screen = 'menu'; rt.setStatus(null); }
        } else if (k === 'escape') { s.screen = 'menu'; rt.setStatus(null); }
        break;

      // ── first-time setup ──
      case 'setupCreds': {
        const fields = this.credFields();
        if (k === 'tab' || k === 'down') s.setupField = (s.setupField + 1) % fields.length;
        else if (k === 'shift+tab' || k === 'up') s.setupField = (s.setupField + fields.length - 1) % fields.length;
        else if (k === 'enter') {
          const missing = fields.find((f) => !(s.setupDrafts[f.key] || '').trim());
          if (missing) rt.setStatus(`${missing.label} is required.`, 'err');
          else this.startSetupProbe(rt);
        } else if (k === 'escape') { s.screen = 'picker'; rt.setStatus(null); }
        else { const key = fields[s.setupField].key; if (k === 'backspace') s.setupDrafts[key] = (s.setupDrafts[key] || '').slice(0, -1); else if (k === 'space') s.setupDrafts[key] = (s.setupDrafts[key] || '') + ' '; else if (k.length === 1) s.setupDrafts[key] = (s.setupDrafts[key] || '') + k; }
        break;
      }
      case 'setupValidating':
        if (k === 'escape') { rt.clearTimers(); s.screen = 'setupCreds'; }
        break;
      case 'setupDialog':
        if (k === 'up') s.dialogIndex = Math.max(0, s.dialogIndex - 1);
        else if (k === 'down') s.dialogIndex = Math.min(2, s.dialogIndex + 1);
        else if (k === 'enter') {
          if (s.dialogIndex === 0) this.startSetupProbe(rt);              // Retry
          else if (s.dialogIndex === 1) s.screen = 'setupCreds';          // Back to edit
          else { s.setupChannel = ''; s.setupAud = 1; s.screen = 'setupChannel'; }  // Save anyway
        } else if (k === 'escape') s.screen = 'setupCreds';
        break;
      case 'setupChannel':
        if (k === 'enter') this.finishSetup(rt);
        else if (k === 'escape') { s.screen = 'setupCreds'; rt.setStatus(null); }
        else if (k === 'backspace') s.setupChannel = s.setupChannel.slice(0, -1);
        else if (k === 'space') s.setupChannel += ' ';
        else if (k.length === 1) s.setupChannel += k;
        break;
    }
  },
};
