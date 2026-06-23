# help.tape (screenshot) — capture the `netclaw --help` usage screen.
#
# Capture-only tape: it has NO post-tape assertion. The screenshots mode
# in run-smoke.sh compares the emitted PNG byte-for-byte against the
# committed baseline at tests/smoke/screenshots/help.approved.png.
#
# The usage screen is fully static (no version string, timestamp, spinner,
# or token counter), so it is a safe screenshot target.
#
# Prepended preamble: tapes/screenshot-preamble.tape (determinism-pinned).

Output "/tmp/tape-shot-help.gif"

Type "netclaw --help"
Enter
# Anchor on the usage header, then sleep to let the full terminal output
# finish rendering. `netclaw --help` writes multiple lines and VHS may not
# have flushed all of them into the terminal buffer by the time Wait+Screen
# matches the first visible line.
Wait+Screen@10s /Usage:/
Sleep 1s
Screenshot "/tmp/shot-help.png"

Type "exit"
Enter
