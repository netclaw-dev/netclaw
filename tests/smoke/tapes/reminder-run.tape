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
# `netclaw daemon start` returns before the daemon listens. Wait for the
# ready probe. Each anchor is a computed value, so the typed command cannot match it.
Type "mkdir -p __NETCLAW_HOME__/logs && netclaw daemon start >__NETCLAW_HOME__/logs/daemon-stdio.log 2>&1 && curl -fsS --retry 30 --retry-all-errors --retry-delay 1 --max-time 2 $NETCLAW_DAEMON_ENDPOINT/api/health/ready && echo DAEMON_$((6*7))"
Enter
Wait+Screen@60s /DAEMON_42/
Type "netclaw reminder create disk-check interval 7d 'Say hello.' && echo REMINDER_$((6*7))"
Enter
Wait+Screen@20s /REMINDER_42/
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
