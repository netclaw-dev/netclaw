# help.tape — smallest possible tape. Validates that the run-native-tape.sh
# pipeline (install-vhs → preamble → tape body → assertion) works
# end-to-end against the native binary. No interactive prompts;
# the only assertion is that vhs reaches the end without timing out.

Output "/tmp/tape-help.gif"

Type "netclaw --help"
Enter
Wait+Screen@10s /Usage:/
Wait+Screen@5s /TAPE\$/

Type "exit"
Enter
