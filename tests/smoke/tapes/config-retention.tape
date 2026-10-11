# Check the native retention editor and its persisted value.
Output "/tmp/tape-config-retention.gif"

Type "mkdir -p $NETCLAW_HOME/config && jq -n '{configVersion:1,Retention:{Logs:{Days:7}}}' > $NETCLAW_HOME/config/netclaw.json"
Enter
Type "netclaw config"
Enter
Wait+Screen@10s /\[Enter\] Select/
Down 10
Wait+Screen@5s /How long the daemon keeps logs and other data/
Enter
Wait+Screen@10s /keep 7 days/

Type "7"
Wait+Screen@5s /keep 7 days \(not saved\)/
Enter
Wait+Screen@5s /Data retention is unchanged/
Type "9"
Wait+Screen@5s /keep 9 days \(not saved\)/
Backspace
Wait+Screen@5s /'' \(not a valid number of days\)/
Type "17"
Wait+Screen@5s /keep 17 days \(not saved\)/
Enter
Wait+Screen@5s /Data retention saved/
Backspace
Wait+Screen@5s /keep 1 day \(not saved\)/

Escape
Wait+Screen@5s /\[Enter\] Select/
Down 10
Wait+Screen@5s /How long the daemon keeps logs and other data/
Enter
Wait+Screen@5s /keep 17 days/
# Send the terminal bracketed-paste protocol at zero delay.
# VHS Copy/Paste emits separate keys and does not exercise a PasteEvent.
Type@0ms "[200~45[201~"
Wait+Screen@5s /keep 45 days \(not saved\)/
Enter
Wait+Screen@5s /Data retention saved/
Escape
Wait+Screen@5s /\[Enter\] Select/
Down 10
Wait+Screen@5s /How long the daemon keeps logs and other data/
Enter
Wait+Screen@5s /keep 45 days/
Ctrl+Q
Wait+Screen@10s /TAPE\$/
Type "exit"
Enter
