# help.tape (screenshot) — capture the `netclaw --help` usage screen.
# No terminal action follows the screenshot.
#
# Capture-only tape: it has NO post-tape assertion. The screenshots mode
# in run-smoke.sh compares the final lossless PNG frame against the
# committed baseline at tests/smoke/screenshots/help.approved.png.
#
# The usage screen is fully static (no version string, timestamp, spinner,
# or token counter), so it is a safe screenshot target.
#
# Prepended preamble: tapes/screenshot-preamble.tape (determinism-pinned).

Output "/tmp/tape-shot-help.gif"

Type "netclaw --help; read -r"
Enter
# The read command keeps the shell from printing a prompt after the help text.
# The final help line proves that the process wrote the complete static frame.
Wait+Screen@10s /Docs & guides:/
Sleep 250ms
