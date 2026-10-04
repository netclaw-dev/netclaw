# reminder-run.tape — smoke `netclaw reminder run <id>`.
#
# The command opens a normal chat. Its hidden first message is
# `/run-reminder <id>`. The smoke model reply proves that the message
# reached the daemon session. The assertion checks the session record.

Output "/tmp/tape-reminder-run.gif"

# ─── Seed a provider, the daemon, and one reminder ──────────────────
Hide
Type "netclaw provider add smoke-llm openai-compatible --endpoint __SMOKE_LLM_ENDPOINT__"
Enter
Wait+Screen@10s /TAPE\$/
Type "netclaw model set main smoke-llm __SMOKE_LLM_MODEL__"
Enter
Wait+Screen@10s /TAPE\$/
Type "netclaw daemon start"
Enter
Wait+Screen@20s /TAPE\$/
Type "netclaw reminder create disk-check interval 7d 'Say hello.' && echo REMINDER_CREATED"
Enter
Wait+Screen@20s /REMINDER_CREATED/
Show

# ─── Launch the test chat ────────────────────────────────────────────
Type "netclaw reminder run disk-check; echo REMINDER_RUN_EXIT=$?"
Enter
Wait+Screen@60s /System: Session started\./
Wait+Screen@60s /Netclaw: Netclaw smoke response\./

# ─── Exit TUI ────────────────────────────────────────────────────────
Ctrl+Q
Wait+Screen@10s /REMINDER_RUN_EXIT=0/

Type "exit"
Enter
