// engine/screen.js
//
// The terminal cell buffer + DOM renderer. This is the prototype's analogue of
// Termina's render surface: components draw glyphs with (fg,bg,bold) into a
// fixed COLS x ROWS grid, and render() flattens each row into coalesced colored
// <span> runs. Box-drawing borders and full-width highlight bars fall out of the
// grid naturally — exactly how the real TUI composes, so back-translation to C#
// stays mechanical.
//
// Measured from the approved VHS baselines (1400x800, FontSize 14, Catppuccin
// Mocha): char pitch ~9px, row height 16px => ~156 cols x 50 rows.

export const COLS = 156;
export const ROWS = 50;

// Catppuccin Mocha. Keep in lockstep with theme.css :root vars.
export const PALETTE = {
  base: '#1e1e2e', mantle: '#181825', crust: '#11111b',
  text: '#cdd6f4', subtext1: '#bac2de', subtext0: '#a6adc8',
  overlay2: '#9399b2', overlay1: '#7f849c', overlay0: '#6c7086',
  surface2: '#585b70', surface1: '#45475a', surface0: '#313244',
  teal: '#94e2d5', sky: '#89dceb', sapphire: '#74c7ec', blue: '#89b4fa',
  lavender: '#b4befe', green: '#a6e3a1', yellow: '#f9e2af', peach: '#fab387',
  maroon: '#eba0ac', red: '#f38ba8', mauve: '#cba6f7', pink: '#f5c2e7',
};

// Semantic names mirroring Termina's Color.* usage, resolved to palette keys.
// Centralizing this lets us recolor the whole prototype in one place.
export const SEM = {
  fg: 'text',          // default foreground / Color.White-ish
  dim: 'overlay0',     // Color.Gray support/help text
  faint: 'surface2',   // Color.BrightBlack key hints / disabled
  accent: 'teal',      // Color.Cyan borders + selection background
  onAccent: 'base',    // text drawn on the accent highlight bar
  ok: 'green', warn: 'yellow', err: 'red',
  fill: 'blue',        // step-indicator filled square
};

function resolve(name) {
  if (!name) return null;
  if (name[0] === '#') return name;
  return PALETTE[name] || PALETTE[SEM[name]] || name;
}

const ESC = { '&': '&amp;', '<': '&lt;', '>': '&gt;' };
const esc = (s) => s.replace(/[&<>]/g, (c) => ESC[c]);

// All box-drawing glyphs. Text renders at font 14 in 16px rows (so descenders
// clear the row below), but a 14px glyph cannot fill a 16px cell, so borders gap.
// We render each box glyph as its own fixed-width cell (class "bx") at font-size =
// row height, exactly how a terminal composes a cell buffer: every border glyph
// fills its cell and fuses with its neighbors — horizontals AND verticals — at a
// uniform weight, with no flow-layout distortion. This is the single source of
// border truth a translator should mirror onto Termina's BorderStyle.Rounded.
const BOX = new Set([
  '─', '│', '╭', '╮', '╰', '╯', '├', '┤', '┬', '┴', '┼',
  '┌', '┐', '└', '┘', '═', '║', '╔', '╗', '╚', '╝',
]);
const isBox = (ch) => BOX.has(ch);

// Box-drawing sets. 'rounded' matches Termina BorderStyle.Rounded.
export const BORDERS = {
  rounded: { tl: '╭', tr: '╮', bl: '╰', br: '╯', h: '─', v: '│' },
  square:  { tl: '┌', tr: '┐', bl: '└', br: '┘', h: '─', v: '│' },
  double:  { tl: '╔', tr: '╗', bl: '╚', br: '╝', h: '═', v: '║' },
};

export class Screen {
  constructor(cols = COLS, rows = ROWS) {
    this.cols = cols;
    this.rows = rows;
    this.cells = new Array(cols * rows);
    this.clear();
  }

  clear(fg = SEM.fg, bg = 'base') {
    for (let i = 0; i < this.cells.length; i++) {
      this.cells[i] = { ch: ' ', fg, bg, bold: false };
    }
  }

  _in(x, y) { return x >= 0 && y >= 0 && x < this.cols && y < this.rows; }

  put(x, y, ch, st = {}) {
    if (!this._in(x, y)) return;
    const cell = this.cells[y * this.cols + x];
    cell.ch = ch;
    if (st.fg !== undefined) cell.fg = st.fg;
    if (st.bg !== undefined) cell.bg = st.bg;
    if (st.bold !== undefined) cell.bold = !!st.bold;
  }

  // Write a string. Returns the x just past the written text.
  text(x, y, str, st = {}) {
    const s = String(str);
    for (let i = 0; i < s.length; i++) this.put(x + i, y, s[i], st);
    return x + s.length;
  }

  fillRect(x, y, w, h, ch, st = {}) {
    for (let yy = y; yy < y + h; yy++)
      for (let xx = x; xx < x + w; xx++) this.put(xx, yy, ch, st);
  }

  hline(x, y, w, ch, st = {}) { for (let i = 0; i < w; i++) this.put(x + i, y, ch, st); }
  vline(x, y, h, ch, st = {}) { for (let i = 0; i < h; i++) this.put(x, y + i, ch, st); }

  // Rounded/box border. Optional title overlaid into the top edge (Termina
  // panels embed the title in the border, e.g. "╭─Netclaw Setup────╮").
  box(x, y, w, h, st = {}, opts = {}) {
    const b = BORDERS[opts.border || 'rounded'];
    const s = { fg: st.fg ?? SEM.accent, bg: st.bg };
    this.put(x, y, b.tl, s);
    this.put(x + w - 1, y, b.tr, s);
    this.put(x, y + h - 1, b.bl, s);
    this.put(x + w - 1, y + h - 1, b.br, s);
    this.hline(x + 1, y, w - 2, b.h, s);
    this.hline(x + 1, y + h - 1, w - 2, b.h, s);
    this.vline(x, y + 1, h - 2, b.v, s);
    this.vline(x + w - 1, y + 1, h - 2, b.v, s);
    if (opts.title) {
      const tcol = opts.titleColor ?? s.fg;
      // "╭─Title──" : one dash, then the title, flush per the baseline render.
      this.text(x + 2, y, opts.title, { fg: tcol, bg: st.bg, bold: opts.titleBold });
    }
    // Inner content rect.
    return { x: x + 1, y: y + 1, w: w - 2, h: h - 2 };
  }

  render(el) {
    const rows = [];
    for (let y = 0; y < this.rows; y++) {
      let html = '';
      let run = null; // text run {fg,bg,bold,text}
      const flush = () => {
        if (!run) return;
        const styles = [`color:${resolve(run.fg)}`];
        if (run.bg && run.bg !== 'base') styles.push(`background:${resolve(run.bg)}`);
        const cls = run.bold ? ' class="b"' : '';
        html += `<span${cls} style="${styles.join(';')}">${esc(run.text)}</span>`;
        run = null;
      };
      for (let x = 0; x < this.cols; x++) {
        const c = this.cells[y * this.cols + x];
        if (isBox(c.ch)) {
          // Each border glyph is its own cell so it fills the row and fuses with
          // neighbors at a uniform weight.
          flush();
          const styles = [`color:${resolve(c.fg)}`];
          if (c.bg && c.bg !== 'base') styles.push(`background:${resolve(c.bg)}`);
          html += `<span class="bx" style="${styles.join(';')}">${esc(c.ch)}</span>`;
        } else if (run && run.fg === c.fg && run.bg === c.bg && run.bold === c.bold) {
          run.text += c.ch;
        } else {
          flush();
          run = { fg: c.fg, bg: c.bg, bold: c.bold, text: c.ch };
        }
      }
      flush();
      rows.push(html);
    }
    el.innerHTML = rows.join('\n');
  }
}
