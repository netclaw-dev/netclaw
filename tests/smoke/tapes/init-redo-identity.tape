# init-redo-identity.tape — existing-install "Redo identity setup" flow.
#
# Seeds a config so `netclaw init` opens the existing-install menu, picks the
# first menu item ("Redo identity setup"), and types through all four identity
# sub-steps (agent name -> comm style -> user name -> timezone) to the saved
# screen. This is forward-progress coverage for the redo flow: a regression that
# blocks advancing past any sub-step (the symptom of the original timezone loop)
# hangs the matching Wait+Screen anchor and fails the tape.
#
# Scope note: this does NOT reproduce the original loop itself. That loop is
# driven by step-content re-renders OUTSIDE the normal step transition (the
# cursor-blink / resize path the IdentityRedoPage fix clears). Deterministic VHS
# input advances field-to-field via step transitions only, so it never triggers
# that accumulation — confirmed by reverting the fix and watching this tape still
# pass. It guards the flow going forward, not that specific instance.
#
# Pairs with tests/smoke/assertions/init-redo-identity.sh, which proves the redo
# finalized (SOUL.md written) without clobbering config (redo never WriteConfig).
#
# No escaped double-quotes (VHS v0.11 limitation); the seeded config is the empty
# object so no quoting is needed, and $NETCLAW_HOME has no spaces.

Output "/tmp/tape-init-redo-identity.gif"

# ─── Seed an existing install ───────────────────────────────────────
Type "mkdir -p $NETCLAW_HOME/config && echo {} > $NETCLAW_HOME/config/netclaw.json"
Enter
Wait+Screen@5s /TAPE\$/

# ─── Launch → existing-install menu (not the bootstrap wizard) ──────
Type "netclaw init"
Enter
Wait+Screen@15s /Existing Netclaw install detected/

# "Redo identity setup" is the first menu item (index 0); Enter selects it.
Enter
Wait+Screen@10s /Agent name/

# ─── Walk the four identity sub-steps via typed keys ────────────────
Type "Sentinel"
Enter
Wait+Screen@10s /Communication style/

# Confirm the highlighted communication style.
Enter
Wait+Screen@10s /Your name/

Type "Pat"
Enter
Wait+Screen@10s /Your timezone/

# The submit that used to loop back to this field forever — it must now
# advance to the saved screen.
Type "UTC"
Enter
Wait+Screen@10s /Identity updated/

# ─── Exit the saved screen back to the shell ────────────────────────
Enter
Wait+Screen@10s /TAPE\$/

Type "exit"
Enter
