# Capture the security posture from `netclaw init`.
# No terminal action follows the screenshot.

Output "/tmp/tape-shot-wizard-security-posture.gif"

Type "netclaw init"
Enter
Wait+Screen@10s /Choose your LLM provider:/
Wait+Screen@5s /8\. Venice\.ai/

# Ollama is the fourth provider.
Down 3
Enter
Wait+Screen@10s /endpoint:/
Right 32
Backspace 32
Type "http://localhost:11434"
Enter

Wait+Screen@45s /Select a model/
Enter
Wait+Screen@10s /Agent name:/
Enter
Wait+Screen@10s /Communication style:/
Enter
Wait+Screen@10s /Your name:/
Type "SmokeTester"
Enter
Wait+Screen@10s /Your timezone:/
Enter

Wait+Screen@10s /Who will interact with this Netclaw instance/
Wait+Screen@5s /Personal/
Sleep 250ms
