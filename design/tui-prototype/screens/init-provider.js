// screens/init-provider.js
//
// The full `netclaw init` Provider step, mirroring ProviderStepView's 7 sub-steps:
//   0 provider select → 1 auth method → 2 credentials → 3 validation(probe)
//   → 4 model select   (5 OAuth device / 6 OAuth browser branch in between)
//
// Effects are faked: credential text is accepted without storing, the probe is a
// scripted ~2.6s spinner that always "succeeds", OAuth auto-completes after a few
// seconds. The point is to capture the animation + dynamic-validation feel that
// the real step produces, so we can judge it before touching C#.

import { initCtx } from '../mock/initctx.js';

// Faked provider registry — mirrors src/Netclaw.Providers/*Descriptor.cs.
// authKind: 'endpoint' (EndpointOnlyAuth), 'apikey' (ApiKeyAuth), 'multi' (MultiAuth).
const PROVIDERS = {
  'anthropic': {
    display: 'Anthropic', authKind: 'apikey',
    guidance: 'https://console.anthropic.com/settings/keys',
    models: ['claude-opus-4-20250514', 'claude-sonnet-4-20250514', 'claude-3-7-sonnet-20250219', 'claude-3-5-haiku-20241022'],
  },
  'github-copilot': {
    display: 'GitHub Copilot', authKind: 'oauth-only',
    methods: [{ label: 'OAuth Device Flow', kind: 'oauth-device' }],
    oauth: { uri: 'https://github.com/login/device', code: 'WDJB-MJHT' },
    models: ['gpt-4o', 'gpt-4.1', 'claude-3.5-sonnet', 'o3-mini', 'gemini-2.0-flash'],
  },
  'ollama': {
    display: 'Ollama', authKind: 'endpoint', endpoint: 'http://localhost:11434',
    models: ['all-minilm', 'qwen2:0.5b'],
  },
  'openai': {
    display: 'OpenAI', authKind: 'multi',
    methods: [
      { label: 'ChatGPT Subscription (recommended)', kind: 'oauth-device' },
      { label: 'ChatGPT Subscription (browser)', kind: 'oauth-pkce' },
      { label: 'API Key (platform.openai.com)', kind: 'apikey' },
    ],
    oauth: { uri: 'https://auth.openai.com/device', code: 'ABCD-1234' },
    models: ['gpt-4o', 'gpt-4o-mini', 'o3', 'o4-mini', 'gpt-4.1'],
  },
  'openai-compatible': {
    display: 'llama.cpp / vLLM', authKind: 'endpoint', endpoint: 'http://localhost:11434',
    models: ['local-model', 'llama-3.3-70b-instruct'],
  },
  'openrouter': {
    display: 'OpenRouter', authKind: 'apikey',
    guidance: 'https://openrouter.ai/keys',
    models: ['anthropic/claude-sonnet-4', 'openai/gpt-4o', 'google/gemini-2.0-flash', 'meta-llama/llama-3.3-70b'],
  },
  'venice-ai': {
    display: 'Venice.ai', authKind: 'apikey',
    models: ['venice-uncensored', 'llama-3.3-70b', 'qwen-2.5-coder-32b'],
  },
};
const ORDER = Object.keys(PROVIDERS); // already alphabetical by type key

const HELP = {
  0: 'Select your LLM provider. Ollama runs locally (no auth required).',
  1: 'Choose how to authenticate with this provider.',
  2: 'Enter your API key. It will be stored in secrets.json.',
  2.5: 'Enter the endpoint URL. No credentials are required.',
  3: 'Validating connection and discovering available models...',
  4: 'Select the model to use for conversations.',
  5: 'Complete the authorization in your browser.',
  6: 'Complete the authorization in your browser.',
};

function authMethodsFor(p) {
  if (p.authKind === 'apikey') return [{ label: 'API Key', kind: 'apikey' }];
  return p.methods || [];
}

export const providerPicker = {
  id: 'init-provider',
  state: {},

  init() {
    this.state = {
      sub: 0,
      providerIndex: 0,
      providerKey: null,
      authIndex: 0,
      authMethods: [],
      authKind: null,
      input: '',
      probeStart: 0,
      probeDone: false,
      modelIndex: 0,
      oauthState: 'waiting', // waiting | success
      oauthStart: 0,
    };
  },

  // Animate during text entry (caret blink), probing, and OAuth waiting.
  isAnimating() {
    const s = this.state;
    return s.sub === 2 || (s.sub === 3 && !s.probeDone) || s.sub === 5 || s.sub === 6;
  },

  get provider() { return PROVIDERS[this.state.providerKey]; },

  // ── transitions ──────────────────────────────────────────────────────────
  confirmProvider(rt) {
    const s = this.state;
    s.providerKey = ORDER[s.providerIndex];
    const p = this.provider;
    if (p.authKind === 'endpoint') { s.authKind = 'endpoint'; this.goCreds(rt); }
    else { s.authMethods = authMethodsFor(p); s.authIndex = 0; s.sub = 1; }
  },
  confirmAuth(rt) {
    const s = this.state;
    const m = s.authMethods[s.authIndex];
    s.authKind = m.kind;
    if (m.kind === 'apikey') this.goCreds(rt);
    else if (m.kind === 'oauth-pkce') this.goBrowserOAuth(rt);
    else this.goDeviceOAuth(rt);
  },
  goCreds() {
    const s = this.state;
    s.sub = 2;
    s.input = s.authKind === 'endpoint' ? (this.provider.endpoint || '') : '';
  },
  goProbe(rt) {
    const s = this.state;
    s.sub = 3;
    s.probeStart = performance.now();
    s.probeDone = false;
    rt.schedule(2600, () => {
      s.probeDone = true;
      rt.schedule(900, () => this.goModels(rt)); // show success frame briefly
    });
  },
  goModels() { const s = this.state; s.sub = 4; s.modelIndex = 0; },
  goDeviceOAuth(rt) {
    const s = this.state;
    s.sub = 5; s.oauthState = 'waiting'; s.oauthStart = performance.now();
    rt.schedule(3500, () => {
      s.oauthState = 'success';
      rt.schedule(1100, () => this.goProbe(rt));
    });
  },
  goBrowserOAuth(rt) {
    const s = this.state;
    s.sub = 6; s.oauthState = 'waiting'; s.oauthStart = performance.now();
    rt.schedule(3500, () => {
      s.oauthState = 'success';
      rt.schedule(1100, () => this.goProbe(rt));
    });
  },

  // ── render ───────────────────────────────────────────────────────────────
  render(scr, rt, W) {
    const r = W.pageFrame(scr, 'Netclaw Setup');
    W.stepIndicator(scr, r, { step: 1, total: 5, title: 'LLM Provider', pct: 20 });
    const s = this.state;

    switch (s.sub) {
      case 0: return this.renderProviders(scr, r, W);
      case 1: return this.renderAuth(scr, r, W);
      case 2: return this.renderCreds(scr, r, W);
      case 3: return this.renderProbe(scr, r, W);
      case 4: return this.renderModels(scr, r, W);
      case 5: return this.renderDeviceOAuth(scr, r, W);
      case 6: return this.renderBrowserOAuth(scr, r, W);
    }
  },

  renderProviders(scr, r, W) {
    W.heading(scr, r, r.y + 2, 'Choose your LLM provider:');
    const items = ORDER.map((k, i) => `${i + 1}. ${PROVIDERS[k].display}`);
    const after = W.selectionList(scr, r, r.y + 3, items, this.state.providerIndex);
    W.helpLines(scr, r, after + 1, [HELP[0]]);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Next  [Esc] Quit  [Ctrl+Q] Quit');
  },

  renderAuth(scr, r, W) {
    const p = this.provider;
    W.heading(scr, r, r.y + 2, `Authentication for ${p.display}:`);
    const items = this.state.authMethods.map((m) => m.label);
    const after = W.selectionList(scr, r, r.y + 3, items, this.state.authIndex);
    W.helpLines(scr, r, after + 1, [HELP[1]]);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderCreds(scr, r, W) {
    const p = this.provider;
    const endpoint = this.state.authKind === 'endpoint';
    const title = endpoint ? `${p.display} endpoint:` : `${p.display} API key:`;
    W.heading(scr, r, r.y + 2, title);
    W.textInputPanel(scr, r, r.y + 3, endpoint ? 'Endpoint' : 'API Key', this.state.input, {
      password: !endpoint,
      placeholder: endpoint ? (p.endpoint || '') : `Enter ${p.display} API key...`,
      focused: true,
      width: endpoint ? 56 : 56,
    });
    W.helpLines(scr, r, r.y + 7, [endpoint ? HELP[2.5] : HELP[2]]);
    W.keyHints(scr, r, '[Enter] Submit  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderProbe(scr, r, W) {
    const s = this.state;
    const provider = s.providerKey;
    if (!s.probeDone) {
      const elapsed = Math.floor((performance.now() - s.probeStart) / 1000);
      W.spinner(scr, r, r.y + 3, `Validating connection to ${provider}...`, 'warn', elapsed);
      W.helpLines(scr, r, r.y + 5, [HELP[3]]);
      W.keyHints(scr, r, '[Esc] Cancel  [Ctrl+Q] Quit');
    } else {
      const n = this.provider.models.length;
      W.line(scr, r, r.y + 3, `✓ Connected! Found ${n} model${n === 1 ? '' : 's'}.`, 'ok');
      W.keyHints(scr, r, '[Ctrl+Q] Quit');
    }
  },

  renderModels(scr, r, W) {
    const models = this.provider.models;
    const items = [...models, 'Enter model ID manually...'];
    W.heading(scr, r, r.y + 2, `Select a model (${models.length} available):`);
    const after = W.selectionList(scr, r, r.y + 3, items, this.state.modelIndex);
    W.helpLines(scr, r, after + 1, [HELP[4]]);
    W.keyHints(scr, r, '[↑/↓] Navigate  [Enter] Select  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderDeviceOAuth(scr, r, W) {
    const s = this.state, p = this.provider;
    W.line(scr, r, r.y + 2, `OAuth Device Flow for ${p.display}`, 'fg', { bold: true });
    if (s.oauthState === 'success') {
      W.line(scr, r, r.y + 4, '✓ Authorization successful!', 'ok');
      W.keyHints(scr, r, '[Ctrl+Q] Quit');
      return;
    }
    W.line(scr, r, r.y + 4, `Visit: ${p.oauth.uri}`, 'accent');
    W.line(scr, r, r.y + 6, `Enter code: ${p.oauth.code}`, 'fg', { bold: true });
    W.line(scr, r, r.y + 8, '[O] open in browser    [C] copy code', 'faint');
    W.spinner(scr, r, r.y + 10, 'Waiting for authorization...', 'warn');
    W.helpLines(scr, r, r.y + 12, [HELP[5]]);
    W.keyHints(scr, r, '[O] Open browser  [C] Copy code  [Esc] Back  [Ctrl+Q] Quit');
  },

  renderBrowserOAuth(scr, r, W) {
    const s = this.state, p = this.provider;
    W.line(scr, r, r.y + 2, `OAuth Login for ${p.display}`, 'fg', { bold: true });
    if (s.oauthState === 'success') {
      W.line(scr, r, r.y + 4, '✔ Authorization successful!', 'ok');
      W.keyHints(scr, r, '[Ctrl+Q] Quit');
      return;
    }
    W.spinner(scr, r, r.y + 4, 'Opening browser for authorization...', 'warn');
    const elapsed = Math.floor((performance.now() - s.oauthStart) / 1000);
    W.line(scr, r, r.y + 6, `Waiting for callback...  (${elapsed}s)`, 'faint');
    W.line(scr, r, r.y + 8, "Can't receive the callback? Paste the redirect URL:", 'faint');
    W.textInputPanel(scr, r, r.y + 9, '', '', { placeholder: 'Paste redirect URL here...', width: 56 });
    W.keyHints(scr, r, '[Esc] Back  [Ctrl+Q] Quit');
  },

  // ── input ────────────────────────────────────────────────────────────────
  onKey(k, rt) {
    const s = this.state;
    switch (s.sub) {
      case 0:
        if (k === 'up') s.providerIndex = Math.max(0, s.providerIndex - 1);
        else if (k === 'down') s.providerIndex = Math.min(ORDER.length - 1, s.providerIndex + 1);
        else if (k === 'enter') this.confirmProvider(rt);
        break;
      case 1:
        if (k === 'up') s.authIndex = Math.max(0, s.authIndex - 1);
        else if (k === 'down') s.authIndex = Math.min(s.authMethods.length - 1, s.authIndex + 1);
        else if (k === 'enter') this.confirmAuth(rt);
        else if (k === 'escape') { s.sub = 0; }
        break;
      case 2:
        if (k === 'enter') this.goProbe(rt);
        else if (k === 'escape') { rt.clearTimers(); s.sub = this.provider.authKind === 'endpoint' ? 0 : 1; }
        else if (k === 'backspace') s.input = s.input.slice(0, -1);
        else if (k === 'space') s.input += ' ';
        else if (k.length === 1) s.input += k;
        break;
      case 3:
        if (k === 'escape') { rt.clearTimers(); s.sub = 2; }
        break;
      case 4:
        if (k === 'up') s.modelIndex = Math.max(0, s.modelIndex - 1);
        else if (k === 'down') s.modelIndex = Math.min(this.provider.models.length, s.modelIndex + 1);
        else if (k === 'enter') {
          initCtx.provider = this.provider.display;
          const m = this.provider.models[s.modelIndex];
          if (m) initCtx.model = m;
          rt.go('init-identity');
        } else if (k === 'escape') { s.sub = 2; }
        break;
      case 5:
      case 6:
        if (k === 'escape') { rt.clearTimers(); s.sub = 1; }
        break;
    }
  },
};
