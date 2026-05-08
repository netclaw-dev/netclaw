# Shared preamble prepended by scripts/smoke/run-tape.sh before each tape body.
#
# Establishes a docker exec session into the netclaw-sandbox container with a
# per-tape NETCLAW_HOME so each tape gets a clean config directory without
# disturbing the smoke compose volume.
#
# The wrapper substitutes:
#   __PROJECT__       — docker compose project name
#   __COMPOSE_FILE__  — path to docker-compose.smoke.yml
#   __TAPE_NAME__     — short name (e.g. init-wizard) used to scope NETCLAW_HOME
#
# After the preamble runs each tape body can assume:
#   * It is inside an interactive bash session in the sandbox container.
#   * NETCLAW_HOME points at an empty per-tape directory.
#   * The shell prompt has been replaced with `TAPE$ ` so screen anchors
#     like /TAPE\$/ work without depending on PS1.
#
# VHS v0.11 does not support escaped quotes inside `Type "..."`, so we
# avoid them entirely by burning the per-tape directory path into the
# preamble at substitution time.

Set Shell "bash"
# Width/Height are PIXELS in VHS, not character columns. At FontSize 14
# this gives roughly 95 cols x 50 rows — enough for the wizard's panels
# without wrapping the typed `docker compose exec` line.
Set Width 1400
Set Height 800
Set FontSize 14
Set Padding 0
Set TypingSpeed 30ms

Hide

# Replace the host vhs-spawned bash with a docker exec into the sandbox.
# Bash inside the netclaw image starts with an empty PS1 (USER netclaw,
# no profile), so we cannot wait on a prompt — we wait briefly to let
# the exec attach, then explicitly set PS1 ourselves so anchors below
# work deterministically.
Type "exec docker compose -p __PROJECT__ -f __COMPOSE_FILE__ exec netclaw-sandbox bash"
Enter
Sleep 1500ms

Type "rm -rf /tmp/tape-__TAPE_NAME__ && mkdir -p /tmp/tape-__TAPE_NAME__"
Enter
Type "export NETCLAW_HOME=/tmp/tape-__TAPE_NAME__"
Enter
Type "export PS1='TAPE$ '"
Enter
Type "clear"
Enter
Wait+Screen /TAPE\$/

Show
