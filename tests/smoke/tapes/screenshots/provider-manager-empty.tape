# Capture the empty provider manager.
# No terminal action follows the screenshot.

Output "/tmp/tape-shot-provider-manager-empty.gif"

Type "netclaw provider"
Enter

# The first list has no state transition before the capture.
Wait+Screen@10s /Ollama/
Wait+Screen@5s /Venice/
Wait+Screen@5s /\[Delete\] Remove/
Sleep 250ms
