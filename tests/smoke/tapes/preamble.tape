# Shared preamble prepended by scripts/smoke/run-native-tape.sh before each
# tape body.
#
# Establishes a plain bash session (no docker) with a per-tape NETCLAW_HOME
# so each tape gets a clean config directory. The netclaw/netclawd binaries
# are run from a host directory put on PATH.
#
# The wrapper substitutes:
#   __NETCLAW_HOME__     — per-tape NETCLAW_HOME directory (host filesystem)
#   __NETCLAW_BIN_DIR__  — directory containing the `netclaw` CLI binary (added to PATH)
#   __NETCLAW_DAEMON__   — absolute path to the `netclawd` daemon binary
#   __TAPE_NAME__        — short name (e.g. init-wizard)
#
# The CLI and daemon publish to separate directories (publish/cli/,
# publish/daemon/), so NETCLAW_DAEMON_PATH cannot be derived from the CLI
# directory — it is substituted explicitly.
#
# After the preamble runs each tape body can assume:
#   * It is inside an interactive bash session on the host.
#   * `netclaw` / `netclawd` are on PATH.
#   * NETCLAW_HOME points at an empty per-tape directory.
#   * NETCLAW_DAEMON_PATH points at the netclawd binary so the CLI's
#     daemon resolver finds it.
#   * The shell prompt has been replaced with `TAPE$ ` so screen anchors
#     like /TAPE\$/ work without depending on PS1.
#
# VHS v0.11 does not support escaped quotes inside `Type "..."`, so we
# avoid them entirely by burning the per-tape directory paths into the
# preamble at substitution time.

Set Shell "bash"
# Width/Height are PIXELS in VHS, not character columns. At FontSize 14
# this gives roughly 95 cols x 50 rows — enough for the wizard's panels
# without wrapping the typed lines.
Set Width 1400
Set Height 800
Set FontSize 14
Set Padding 0
Set TypingSpeed 30ms

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
