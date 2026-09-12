# Shared preamble prepended by scripts/smoke/run-native-tape.sh before each
# SCREENSHOT tape body (run-smoke.sh `screenshots` mode sets TAPE_PREAMBLE
# to this file).
#
# This is a determinism-pinned variant of preamble.tape. It establishes the
# same plain bash session with a per-tape NETCLAW_HOME, but adds extra pins
# so a captured PNG is byte-stable across runs:
#
#   * `Set CursorBlink false` — a blinking cursor toggles pixels between
#     frames; with blink off the cursor cell is deterministic.
#   * `Set Theme "Catppuccin Mocha"` — pin a named built-in VHS theme so a
#     change to VHS's *default* theme cannot silently drift every baseline.
#     Bumping VHS or this theme is a deliberate, reviewed baseline refresh.
#
# We deliberately do NOT `Set FontFamily` to a host font — VHS ships its own
# bundled font, and relying on it keeps the render identical regardless of
# which fonts the CI runner or a developer's machine happens to have. With a
# pinned VHS version + theme + dimensions + font size + cursor settings the
# rendered terminal is reproducible.
#
# The wrapper substitutes the same placeholders as preamble.tape:
#   __NETCLAW_HOME__     — per-tape NETCLAW_HOME directory (host filesystem)
#   __NETCLAW_BIN_DIR__  — directory containing the `netclaw` CLI binary
#   __NETCLAW_DAEMON__   — absolute path to the `netclawd` daemon binary
#   __TAPE_NAME__        — short name (e.g. wizard-screens)
#
# VHS v0.11 does not support escaped quotes inside `Type "..."`, so the
# per-tape directory paths are burned into the preamble at substitution time.

Set Shell "bash"
# Width/Height are PIXELS in VHS, not character columns. Kept identical to
# preamble.tape (1400x800 at FontSize 14 ≈ 95 cols x 50 rows) so screenshot
# captures use the same geometry the flow tapes were authored against.
Set Width 1400
Set Height 800
Set FontSize 14
Set Padding 0
Set TypingSpeed 30ms

# Determinism pins for stable screenshot pixels.
Set CursorBlink false
Set Framerate 60
Set Theme "Catppuccin Mocha"

# Record every frame as a lossless PNG. The harness selects the final frame
# after VHS exits, because the Screenshot command can read a stale browser frame.
Output "/tmp/shot-frames-__TAPE_NAME__/"

Hide

# The host vhs-spawned bash may carry an unhelpful PS1; we set our own
# below so anchors are deterministic.
Type "rm -rf __NETCLAW_HOME__ && mkdir -p __NETCLAW_HOME__"
Enter
Type "export PATH=__NETCLAW_BIN_DIR__:$PATH"
Enter
Type "export NETCLAW_HOME=__NETCLAW_HOME__"
Enter
Type "export NETCLAW_DAEMON_PATH=__NETCLAW_DAEMON__"
Enter
Type "export PS1='TAPE$ '"
Enter
Type "clear"
Enter
Wait+Screen /TAPE\$/

Show
