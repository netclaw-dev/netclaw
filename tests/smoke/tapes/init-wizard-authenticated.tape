# This tape proves that the wizard accepts a typed optional key. The protected
# smoke endpoint accepts only that key for discovery and the initial chat turn.

Output "/tmp/tape-init-wizard-authenticated.gif"

Type "netclaw init"
Enter

Wait+Screen@10s /Choose your LLM provider:/
Down 5
Enter

Wait+Screen@10s /endpoint:/
Right 32
Backspace 32
Type "__SMOKE_LLM_PROTECTED_ENDPOINT__"
Enter

Wait+Screen@10s /API key \(optional\)/
Type "__SMOKE_LLM_PROTECTED_API_KEY__"
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
Enter

Wait+Screen@60s /System: Session started\./
Wait+Screen@60s /Netclaw: Netclaw smoke response\./
Ctrl+Q
