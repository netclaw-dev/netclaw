# init-existing.tape — existing-install menu + double-confirmed start-over.
#
# Seeds a config so `netclaw init` opens the existing-install action menu
# instead of the bootstrap wizard (simplify-netclaw-init §3-4). Drives into
# the start-over scope + first confirmation, backs out without confirming,
# then cancels — proving the menu renders, the reset is double-confirmed, and
# config is untouched until both confirmations pass.
#
# No escaped double-quotes (VHS v0.11 limitation); $NETCLAW_HOME has no spaces.

Output "/tmp/tape-init-existing.gif"

# ─── Seed an existing install ───────────────────────────────────────
Type "mkdir -p $NETCLAW_HOME/config && echo {} > $NETCLAW_HOME/config/netclaw.json"
Enter
Wait+Screen@5s /TAPE\$/

# ─── Launch → existing-install menu (not the wizard) ────────────────
Type "netclaw init"
Enter
Wait+Screen@15s /Existing Netclaw install detected/

# Move to "Start over from scratch" (3rd item) and open the scope chooser.
Down 2
Enter
Wait+Screen@10s /choose a scope/

# "Full reset" (2nd) → first confirmation.
Down
Enter
Wait+Screen@10s /confirmation 1 of 2/

# Default selection is Cancel, and Esc backs out without deleting anything.
Escape
Wait+Screen@10s /choose a scope/

# Cancel (3rd) returns to the menu.
Down 2
Enter
Wait+Screen@10s /Existing Netclaw install detected/

# Cancel (4th) exits init with config untouched.
Down 3
Enter
Wait+Screen@10s /TAPE\$/

# The config must still exist — we never confirmed a reset.
Type "test -f $NETCLAW_HOME/config/netclaw.json && echo MENU_OK"
Enter
Wait+Screen@5s /MENU_OK/

Type "exit"
Enter
