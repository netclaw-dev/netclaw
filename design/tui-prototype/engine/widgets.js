// engine/widgets.js
//
// Higher-level primitives mirroring the real Termina/Netclaw view helpers so the
// prototype maps back to named C# constructs:
//   pageFrame     <- NetclawTuiChrome.BuildPageFrame (full-screen titled panel)
//   stepIndicator <- InitWizardPage step bar ("Step N of T: Title [■□...] P%")
//   selectionList <- SelectionListNode (full-width highlight bar)
//   helpLines     <- GetHelpText() dim support text
//   keyHints      <- BuildKeyHintLine (dim footer)
//   statusLine    <- BuildStatusLine (colored status row)

import { SEM } from './screen.js';

const INDENT = 2; // Termina view strings are indented 2 cols under the border.

// Full-screen titled panel. Returns the inner content rect.
export function pageFrame(scr, title) {
  return scr.box(0, 0, scr.cols, scr.rows, { fg: SEM.accent }, {
    border: 'rounded', title, titleColor: SEM.accent,
  });
}

// Step progress line. The square bar sits at a fixed column so it stays aligned
// across steps regardless of title length (matches the baseline render).
export function stepIndicator(scr, rect, { step, total, title, pct, barCol = 58, squares = 10 }) {
  const y = rect.y;
  const label = `Step ${step} of ${total}: ${title}`;
  scr.text(rect.x + INDENT, y, label, { fg: SEM.fg, bold: true });

  let x = rect.x + barCol;
  x = scr.text(x, y, '[', { fg: SEM.fg });
  const filled = Math.round((pct / 100) * squares);
  for (let i = 0; i < squares; i++) {
    scr.put(x++, y, i < filled ? '■' : '□', { fg: i < filled ? SEM.fill : SEM.faint });
  }
  x = scr.text(x, y, ']', { fg: SEM.fg });
  scr.text(x + 1, y, `${pct}%`, { fg: SEM.fg });
}

// Heading line (white).
export function heading(scr, rect, y, str, st = {}) {
  return scr.text(rect.x + INDENT, y, str, { fg: SEM.fg, ...st });
}

// Single-select list with a full-width highlight bar on the active row.
// items: array of strings (already formatted, e.g. "1. Anthropic").
// opts.barBg/barFg override the highlight colors (e.g. yellow for dialogs).
// opts.disabled(i) dims a row. Returns the y after the last row.
export function selectionList(scr, rect, y, items, index, opts = {}) {
  const left = rect.x;
  const barBg = opts.barBg || SEM.accent;
  const barFg = opts.barFg || SEM.onAccent;
  for (let i = 0; i < items.length; i++) {
    const yy = y + i;
    if (i === index) {
      scr.fillRect(left, yy, rect.w, 1, ' ', { bg: barBg, fg: barFg });
      scr.text(left, yy, items[i], { bg: barBg, fg: barFg });
    } else {
      const fg = opts.disabled && opts.disabled(i) ? SEM.faint : (opts.fg || SEM.fg);
      scr.text(left, yy, items[i], { fg });
    }
  }
  return y + items.length;
}

// Dim multi-line support/help text. Each entry is one line.
export function helpLines(scr, rect, y, lines, st = {}) {
  lines.forEach((line, i) => {
    scr.text(rect.x + INDENT, y + i, line, { fg: SEM.dim, ...st });
  });
  return y + lines.length;
}

// Colored status row (defaults to the row above the key-hint footer).
export function statusLine(scr, rect, text, color = SEM.ok, y = rect.y + rect.h - 2) {
  if (!text) return;
  scr.text(rect.x + INDENT, y, text, { fg: color });
}

// Dim key-hint footer pinned to the bottom inner row.
export function keyHints(scr, rect, text) {
  scr.text(rect.x + INDENT, rect.y + rect.h - 1, text, { fg: SEM.faint });
}

// Termina SpinnerStyle.Dots — the 10-frame braille set, ~80ms/frame. Self-
// animating in the real TUI; here the frame is derived from the wall clock so it
// animates whenever the runtime re-renders (see rt tick loop).
const SPIN_FRAMES = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

// Spinner + label (+ optional "(Ns)" elapsed), 2-col indent — SpinnerViews.WithElapsed.
export function spinner(scr, rect, y, label, color = SEM.warn, elapsedSec = 0) {
  const frame = SPIN_FRAMES[Math.floor(performance.now() / 80) % SPIN_FRAMES.length];
  let x = rect.x + INDENT;
  x = scr.text(x, y, frame, { fg: color });
  x = scr.text(x, y, ' ' + label, { fg: color });
  if (elapsedSec > 0) scr.text(x, y, ` (${elapsedSec}s)`, { fg: color });
  return y;
}

// Gray-bordered single-line input panel with the label in the top border —
// NetclawTuiChrome.BuildTextInputPanel (Color.Gray border, height 3). Password
// mode masks with bullets; a block caret blinks when focused.
export function textInputPanel(scr, rect, y, title, value, opts = {}) {
  const w = opts.width || (rect.w - INDENT * 2);
  const x = rect.x + INDENT;
  const inner = scr.box(x, y, w, 3, { fg: 'overlay1' }, {
    border: 'rounded', title, titleColor: 'overlay1',
  });
  const cap = w - 4;
  const shown = value && value.length
    ? (opts.password ? '•'.repeat(value.length) : value)
    : '';
  if (shown) scr.text(inner.x + 1, inner.y, shown.slice(-cap), { fg: 'text' });
  else if (opts.placeholder) scr.text(inner.x + 1, inner.y, opts.placeholder.slice(0, cap), { fg: 'overlay0' });
  if (opts.focused && Math.floor(performance.now() / 530) % 2 === 0) {
    const cx = inner.x + 1 + Math.min(shown.length, cap);
    scr.put(cx, inner.y, ' ', { bg: 'text', fg: 'base' });
  }
  return y + 3;
}

// A plain text line at an arbitrary row with a semantic color.
export function line(scr, rect, y, str, color = SEM.fg, st = {}) {
  return scr.text(rect.x + INDENT, y, str, { fg: color, ...st });
}
