# TUI-002: Chat Visual Grammar

Source PRDs: `PRD-004`, `PRD-009`

Revised: 2026-08-12

## Design Intent

Netclaw chat uses a quiet conversation grammar.
The design gives prose priority over execution detail.
One user prompt and one Netclaw reply form one Turn.
Each event in that exchange belongs to the same Turn.

This grammar has five goals:

- Make each Turn easy to scan.
- Show useful motion before the first text delta.
- Keep tools inside the Netclaw Reply Block.
- Keep the Composer available while Netclaw works.
- Keep terminal selection free of decorative trim.

## Hierarchy

The interface uses five visual levels.

| Level | Content | Rule |
|-------|---------|------|
| 1 | User prompt and Netclaw prose | Give this content the strongest contrast and widest measure. |
| 2 | Live work and decisions | Nest this content inside the current Reply Block. |
| 3 | Tool receipts and files | Use compact rows under the prose that caused them. |
| 4 | Time, model, tokens, and identity | Use muted text and remove it first at narrow widths. |
| 5 | Key hints | Keep this content on the Pulse Line. |

No tool, approval, or parallel group can appear as a peer of the Reply Block.
The Reply Block is the unit of comprehension, settlement, inspection, and copy.

## Named Regions

| Region | Purpose | Lifetime |
|--------|---------|----------|
| Session Strip | Shows the session, model, context, and connection | Persistent bottom dock |
| Transcript | Holds immutable settled Turns | Terminal scrollback |
| Turn | Groups one user prompt and one Reply Block | Settled after the reply ends |
| Reply Block | Owns Netclaw prose and all work for one Turn | Live, then immutable |
| Reply Passage | Groups one model step with its prose and Work Trace | Nested in the Reply Block |
| Work Trace | Shows transient thought, tool, and sub-agent activity | Nested in the Reply Block |
| Decision Sheet | Owns one approval request and its choices | Nested in the Reply Block |
| Queue Shelf | Shows prompts that wait behind the current Turn | Live |
| Composer | Accepts the next prompt | Live, except during a decision |
| Pulse Line | Shows `Thinking.` state and valid keys | Live |
| Inspector | Shows complete safe detail for one Turn or event | Temporary viewport |

## Selection Rule

The settled Transcript contains no corner, border, rail, or divider glyphs.
The live region also avoids these glyphs where practical.
Cell background can define a surface because terminal selection does not copy color.

Visible text must have semantic value when the user selects it.
The Transcript omits spinners, selection markers, hint text, and elapsed-time frames.
The Inspector provides semantic copy without ANSI bytes or decorative text.

## Visual Tokens

The application maps these roles to the active terminal palette.
The application does not require one fixed theme.

| Role | Purpose |
|------|---------|
| Canvas | The normal terminal background |
| Human surface | The user prompt and queued prompts |
| Reply surface | The current Netclaw Reply Block |
| Work surface | A nested Work Trace or Decision Sheet |
| Primary | Netclaw identity and active controls |
| Human | User identity and user prompts |
| Success | A useful completed result |
| Warning | A decision or degraded result |
| Danger | A failure or denial |
| Muted | Time, metadata, receipts, and key hints |

Bold text identifies a speaker, an active state, or the selected decision.
The design uses no more than three emphasis colors in one region.

## Spacing Rhythm

- The viewport uses a two-cell left margin at 60 columns or more.
- A Turn uses one blank line before the user prompt.
- The Reply Block follows its prompt without a large vertical break.
- Reply prose uses a two-cell indent.
- Work Trace rows use a four-cell indent.
- Child tools and sub-agents use a six-cell indent.
- The Composer uses two text rows plus the Pulse Line.
- A settled Turn uses one blank line before the next Turn.
- A speaker change uses one blank line between the settled blocks.
- The bottom dock uses one blank line between its stacked interactive surfaces.

## Reply Block Grammar

The Reply Block starts when the session accepts a user prompt.
The block stays live until the Turn ends.
Assistant deltas extend prose inside the same block.

The Work Trace uses the model-supplied tool rationale as each action title.
The tool name remains secondary metadata.
The client does not infer an action title from tool arguments.
It does not expose raw JSON in the Transcript.
A new tool call without a rationale fails before tool dispatch.
An old transcript entry can show that its rationale is unavailable.

Examples:

```text
  Thinking about the deployment layout
    ⠹ Search deployment settings
      shell_execute · grep context window
    ✓ Read the session manifest · 0.2s
    ! Protected config blocked the request
```

The live spinner replaces its prior frame in place.
The tool name is secondary detail.
The description explains the current action.
The row can show safe fly-by text after the action.

The settled Reply Block collapses successful work into receipts.
Failures remain visible because they can change the reply meaning.

```text
  ✓ Inspected deployment settings · 3 tools · 1.7s
  ! Protected config prevented one check
```

The Inspector retains each call identity, argument, result, duration, and parent relation.

## Chronology Grammar

One Reply Block can contain multiple Reply Passages.
Each Reply Passage represents one model step in the tool loop.
New model prose starts the next passage without starting a new user Turn.

A completed call remains visible as a receipt while later calls remain active.
Parallel calls share one group and retain separate lifecycle states.
The final settled Turn replaces transient Work Trace rows with one compact receipt.

The current session contract preserves order between model steps.
It does not preserve exact text and tool order inside one model response.
That capability requires an additive ordered-segment contract.

## Composer and Queue Grammar

The Composer stays visible while the model or a tool works.
Enter sends a later prompt to the session queue.
The Queue Shelf shows each accepted prompt above the Composer.

The Queue Shelf does not interrupt the current Reply Block.
All displayed prompts enter the next model call in FIFO order.
The session actor promotes the complete set together after the current Turn.
The client does not send one queued prompt after each completed Turn.

A Decision Sheet is the only state that hides the Composer.
This exception prevents prompt text from reaching an approval control.

## Pulse Grammar

The Session Strip stays in the persistent bottom dock.
It stays beside the Composer or Decision Sheet in that dock.
The Pulse Line remains the bottom row.

The bottom-left Pulse Line shows model wait state with this exact sequence:

```text
Thinking.  →  Thinking..  →  Thinking...
```

The pulse continues until text, work, a decision, an error, or completion changes the state.
The right side shows only keys that work in the current state.
The pulse reserves a fixed 12-character slot with one character of right padding.
Only the dots change, so the key hints and the complete row remain stationary.

The Pulse Line uses these state words:

| State | Text |
|-------|------|
| Model wait or text stream | `Thinking.` pulse |
| Tool or sub-agent work | `Working.` pulse |
| Queued prompt accepted | `Queued 1` |
| Approval | `Decision needed` |
| Idle | `Ready` |
| Connection loss | `Disconnected` |

## ASCII Mockup: Live Reply with a Queued Prompt

```text
You  13:35
  Find the configured context window.

Netclaw  13:35                                                     LIVE
  I will inspect the deployment settings and the active session.

    ⠹ Search deployment settings
      shell_execute · grep context and model values

    Parallel work · 2 calls
      ✓ Read session manifest · 0.2s
      ⠋ Inspect model configuration · file_list

Queued  1
  Then tell me which setting wins.

NETCLAW  Casual Greeting Exchange  deepseek-v4-flash-dspark  18%  connected

MESSAGE
  Ask Netclaw...

Thinking..   Enter send  Shift+Enter newline  Esc x2 clear  Ctrl+O inspect
```

The user sees prose first.
The tool rows explain intent and stay inside the Netclaw Reply Block.
The Composer remains available during the active Turn.

## ASCII Mockup: Settled Turn

```text
You  13:35
  Find the configured context window.

Netclaw  13:35
  I inspected the deployment settings and the active session.

    ✓ Inspected configuration · 3 tools · 1.7s
    ! One protected path blocked direct access

  The main model uses a 131,072-token context window.
  The named model definition overrides the provider default.

NETCLAW  Casual Greeting Exchange  deepseek-v4-flash-dspark  19%  connected

MESSAGE
  Ask Netclaw...

Ready   Enter send  Shift+Enter newline  Esc x2 clear  Ctrl+O inspect
```

The settled Turn prints as one immutable block.
Individual tool cards do not enter the Transcript.

## ASCII Mockup: Decision Sheet

```text
You  13:35
  Find the configured context window.

Netclaw  13:35                                                     WAITING
  I need permission to inspect a protected session path.

NETCLAW  Casual Greeting Exchange  deepseek-v4-flash-dspark  18%  connected

  Approval required
    Requester  Netclaw
    Action     Run shell_execute
    Target     grep context values in the current session
    Scope      this exact command in this session directory

    1. Allow once
    2. Allow for this chat
    3. Deny

Decision needed   Up/Down select  Enter confirm  Ctrl+O details  Esc deny
```

The Decision Sheet stays inside the current Reply Block.
The Sheet hides the Composer until the user makes a decision.

## Serial Approval Queue

Netclaw shows one Decision Sheet at a time.
The queue head owns the sheet and keyboard input.
Other approval requests stay in the Work Trace with a `Waiting` state.
The sheet header shows `1 of N` when the queue contains multiple requests.
Each choice targets one exact `CallId`.
Netclaw waits for that approval outcome before it shows the next sheet.

```text
  Decision  List workspaces  · shell_execute  awaiting decision
  Waiting   Run diagnostics  · shell_execute  decision 2 of 2

  Approval required  1 of 2  Netclaw requests permission to run shell_execute
```

A grant does not resolve another queued request.
The daemon can authorize a later request if a persistent grant covers it.
A denial affects only the request in the current sheet.
The next sheet keeps keyboard focus when it replaces the prior sheet.

## ASCII Mockup: Narrow Form at 48 Columns

```text
NETCLAW  Casual Greeting  18%

You  13:35
  Find the context window.

Netclaw  13:35                         LIVE
  I will inspect the settings.

    ⠹ Search deployment settings
      shell_execute · grep context

Queued  1
  Tell me which setting wins.

MESSAGE
  Ask Netclaw...

Thinking..   Enter send  Esc x2 clear
```

The narrow form removes the model, connection, duration, and tool kind first.
It keeps the speaker, action, outcome, queue, input, and decision state.

## State Flow

```text
Composer --Enter--> Reply Block --Turn ends--> Settled Turn
    |                    |                         |
    |                    +--tool--> Work Trace     +--> one queued batch
    |                    +--approval--> Decision Sheet
    +--Enter while live--> Queue Shelf
```

The Reply Block receives all events for the current Turn.
The Composer remains live during model, tool, and sub-agent work.
The Decision Sheet owns input before the Composer.
The approval queue exposes only its head as a Decision Sheet.

## Content Rules

- Use a verb-first action description for each live row.
- Put the tool name after the action or omit it at narrow widths.
- Show safe fly-by text only when it helps the operator predict progress.
- Keep assistant prose at stronger contrast than tool detail.
- Do not show raw JSON in the Transcript.
- Do not give each event a separate header or surface.
- Do not repeat `Tool`, `Approval`, or `Parallel tools` as peer cards.
- Do not put a complete command on the Pulse Line.
- Do not truncate a security decision target.
- Keep complete safe detail in the Inspector.

## Responsive Rules

| Width | Session Strip | Work Trace | Pulse Line |
|-------|---------------|------------|------------|
| 120+ | session, model, context, connection | action, fly-by text, tool, duration | full key names |
| 80-119 | session, model, context | action, fly-by text, duration | common key names |
| 60-79 | session, context | action and short outcome | compact key names |
| 40-59 | session suffix, context | clipped action and outcome | essential keys only |

No responsive rule moves an event outside its Reply Block.
Long content remains available through the Inspector and semantic copy.

## Mockup Set

The current SVG files show the first quiet-console concept.
They need revision before they become acceptance targets:

- `mockups/chat-quiet-normal.svg`
- `mockups/chat-quiet-active.svg`
- `mockups/chat-quiet-approval.svg`
- `mockups/chat-quiet-inspector.svg`

The ASCII mockups in this document are the current hierarchy authority.
The team will replace the SVG set after review.
