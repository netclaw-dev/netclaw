# The durable assertion checks the exact input after an immediate quit.
Output "/tmp/tape-chat-admission-quit.gif"

Hide
Type "netclaw provider add smoke-llm openai-compatible --endpoint __SMOKE_LLM_ENDPOINT__ && echo PROVIDER_$((6*7))"
Enter
Wait+Screen@30s /PROVIDER_42/
Type "netclaw model set main smoke-llm __SMOKE_LLM_MODEL__ && echo MODEL_$((6*7))"
Enter
Wait+Screen@30s /MODEL_42/
Type "netclaw daemon start && curl -fsS --retry 30 --retry-all-errors --retry-delay 1 --max-time 2 $NETCLAW_DAEMON_ENDPOINT/api/health/ready && echo DAEMON_$((6*7))"
Enter
Wait+Screen@60s /DAEMON_42/
Show

Type "netclaw chat; echo EMPTY_EXIT_$((6*7)):$?"
Enter
Wait+Screen@30s /Netclaw Chat/
Ctrl+Q
Wait+Screen@10s /EMPTY_EXIT_42:0/
Type "netclaw sessions; echo PICKER_EXIT_$((6*7)):$?"
Enter
Wait+Screen@30s /No sessions found/
Type "n"
Wait+Screen@30s /Netclaw Chat/
Ctrl+Q
Wait+Screen@10s /PICKER_EXIT_42:0/
Type "netclaw sessions --json > __NETCLAW_HOME__/sessions-empty.json && echo EMPTY_CHECK_$((6*7))"
Enter
Wait+Screen@10s /EMPTY_CHECK_42/

Type "netclaw chat; echo CHAT_EXIT_$((6*7)):$?"
Enter
Wait+Screen@30s /Netclaw Chat/
Type "native-admission-before-quit-marker"
Wait+Screen@10s /native-admission-before-quit-marker/
Enter
Ctrl+Q
Wait+Screen@10s /CHAT_EXIT_42:0/

# Resume through the real session picker after the first CLI exits.
Type "netclaw sessions; echo RESUME_EXIT_$((6*7)):$?"
Enter
Wait+Screen@30s /Resume/
Enter
Wait+Screen@30s /Netclaw Chat/
Type "native-resume-marker"
Wait+Screen@10s /native-resume-marker/
Enter
Wait+Screen@30s /Netclaw: Netclaw smoke response\./
Ctrl+Q
Wait+Screen@10s /RESUME_EXIT_42:0/
Type "netclaw sessions --json > __NETCLAW_HOME__/sessions-final.json && echo FINAL_CHECK_$((6*7))"
Enter
Wait+Screen@10s /FINAL_CHECK_42/
Type "exit"
Enter
