# Shared preamble prepended by scripts/smoke/run-tape.sh before each tape body.
#
# Establishes a docker exec session into the netclaw-sandbox container with a
# per-tape NETCLAW_HOME so each tape gets a clean config directory without
# disturbing the smoke compose volume.
#
# The wrapper substitutes:
#   __PROJECT__       — docker compose project name (e.g. netclaw-smoke-tapes)
#   __COMPOSE_FILE__  — path to docker-compose.smoke.yml
#   __TAPE_NAME__     — short name (e.g. init-wizard) used to scope NETCLAW_HOME
#
# Tapes that include this preamble can assume:
#   * They are inside an interactive bash session in the sandbox container.
#   * NETCLAW_HOME points at an empty per-tape directory.
#   * The shell prompt has been replaced with a deterministic marker so
#     Wait+Screen anchors (e.g. /TAPE\$/) work without depending on PS1.

Set Shell "bash"
Set Width 100
Set Height 30
Set FontSize 14
Set Padding 0
Set TypingSpeed 30ms

Hide

Type "exec docker compose -p __PROJECT__ -f __COMPOSE_FILE__ exec netclaw-sandbox bash --noprofile --norc"
Enter
Wait+Screen /[#$] $/

Type "export NETCLAW_HOME=/tmp/tape-__TAPE_NAME__"
Enter
Type "rm -rf \"$NETCLAW_HOME\" && mkdir -p \"$NETCLAW_HOME\""
Enter
Type "export PS1='TAPE$ '"
Enter
Type "clear"
Enter
Wait+Screen /TAPE\$ $/

Show
