# Capture the provider picker from `netclaw init`.
# No terminal action follows the screenshot.

Output "/tmp/tape-shot-wizard-provider-picker.gif"

Type "netclaw init"
Enter
Wait+Screen@10s /Choose your LLM provider:/
Wait+Screen@5s /8\. Venice\.ai/
Sleep 250ms
