# config-workspaces-picker.tape — Workspaces Directory picker flow.
#
# The directory picker (Termina FilePickerNode) leaves a scroll region the vhs
# terminal emulator does not reset on exit, which breaks a SECOND netclaw launch
# in the same recording. So this picker flow lives in its own tape and ends right
# after the save — the post-tape assertion validates the persisted config.

Output "/tmp/tape-config-workspaces-picker.gif"

# Seed installed config + a workspaces tree to pick from. Workspaces.Directory
# points at an existing dir so the picker opens there; pick the lone "picked" subdir.
Type "export WS=$HOME/ws"
Enter
Type "mkdir -p $NETCLAW_HOME/config $WS/picked"
Enter
Type "jq -n '{configVersion:1, Workspaces:{Directory: env.WS}}' > $NETCLAW_HOME/config/netclaw.json"
Enter

Type "netclaw config"
Enter
Wait+Screen@10s /Settings Areas/

# Workspaces Directory is after Security & Access.
Down 9
Enter
Wait+Screen@10s /Workspaces Directory/
Wait+Screen@5s /Choose the workspaces directory/
# The picker opens at $HOME/ws; the lone "picked" subdir is highlighted. Space chooses it.
Wait+Screen@5s /picked/
Space
Wait+Screen@10s /Workspaces Directory saved/

# End the tape immediately (no second launch). The post-tape assertion verifies the config.
Ctrl+Q
Type "exit"
Enter
