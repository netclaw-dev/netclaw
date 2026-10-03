# Chat TUI

`netclaw chat` uses the terminal primary buffer. The terminal owns scrollback
and mouse-wheel scroll. Stable transcript text has no outer border.

Use these keys:

- `Enter` sends the prompt.
- `Shift+Enter` adds a new line.
- `Up` and `Down` recall prompts and restore the current draft.
- `Esc x2` clears the prompt.
- `Ctrl+O` opens the Inspector when chat is idle.
- `Y` copies one Inspector event. `Shift+Y` copies its complete turn.
- `Ctrl+O` expands or collapses an approval detail view.
- `Esc` denies an approval. It also closes the Inspector.
- `Ctrl+Q` exits chat.

The Composer stays available while the model or a tool works. A prompt that you
send during active work enters the Queue Shelf for the next turn. An approval
gate replaces the Composer until the user makes a decision.

Netclaw shows one approval gate at a time. Parallel approval requests enter one
serial queue. The queue head owns the gate and keyboard input. Other requests
stay visible in the Work Trace with a `Waiting` state. A decision targets one
exact tool call. Netclaw waits for its outcome before it shows the next gate.
A persistent grant can let the daemon authorize a later queued request.

The Session Strip stays in the persistent bottom dock with the Composer or
approval gate. The Pulse Line stays at the bottom and shows the current wait
state.

The Work Trace keeps tools inside the current Netclaw Reply Block. Each tool
uses the model-supplied rationale as its primary title. The tool name remains
secondary detail. Safe activity summaries can replace fly-by text while a tool
runs. Parallel calls remain separate rows with separate states.

New model prose after a tool result starts a new Reply Passage in the same user
turn. A completed call remains visible as a compact receipt while later work
continues. The final settled turn replaces transient work with a short receipt.

The Inspector shows complete semantic event text. It omits display borders from
copy output. A failed copy keeps the event selected and shows a visible error.

Use `netclaw sessions` to select a saved session. Netclaw closes the session
picker and opens that session in the same primary-buffer chat view.
