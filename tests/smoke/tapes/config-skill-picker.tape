# config-skill-picker.tape — Skill Sources "add a local folder" picker flow.
#
# The directory picker (Termina FilePickerNode) leaves a scroll region the vhs
# terminal emulator does not reset on exit, which breaks a SECOND netclaw launch
# in the same recording. So this picker flow lives in its own tape and ends right
# after the save — the post-tape assertion validates the persisted config.

Output "/tmp/tape-config-skill-picker.gif"

# Seed minimal installed config and a local skill directory under HOME — the
# picker opens at HOME, so the seeded folder is the lone entry to choose.
Type "mkdir -p $NETCLAW_HOME/config $HOME/netclaw-smoke-skill-picker"
Enter
Type "jq -n '{configVersion:1}' > $NETCLAW_HOME/config/netclaw.json"
Enter

Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/
Down 4
Enter
Wait+Screen@10s /Skill Sources/
Wait+Screen@5s /Places Netclaw loads skills from/
Enter
Wait+Screen@10s /Add a local skill folder/
# The picker opens at $HOME; the seeded folder is highlighted. Space chooses it.
Wait+Screen@5s /netclaw-smoke-skill-picker/
Space
Wait+Screen@10s /Allow symlinks inside this folder/
Enter
Wait+Screen@10s /Review local folder source/
Enter
Wait+Screen@10s /Added local skill folder/

# End the tape immediately (no second launch). The post-tape assertion verifies the config.
Ctrl+Q
Type "exit"
Enter
