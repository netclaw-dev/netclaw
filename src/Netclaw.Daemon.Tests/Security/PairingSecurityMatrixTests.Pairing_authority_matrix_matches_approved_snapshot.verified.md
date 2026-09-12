| Exposure mode | Bearer credential | Proof | HTTP result | Creates code |
|---|---|---|---|---|
| Local | none | valid | 200 | True |
| Local | none | missing | 401 | False |
| Local | none | changed | 401 | False |
| Local | none | cross-home | 401 | False |
| Local | none | stale | 401 | False |
| Local | none | future | 401 | False |
| Local | none | malformed | 401 | False |
| Local | none | unsupported-version | 400 version | False |
| Local | none | wrong-operation | 401 | False |
| Local | none | replay | 401 | False |
| Local | device | valid | 200 | True |
| Local | device | missing | 401 | False |
| Local | device | changed | 401 | False |
| Local | device | cross-home | 401 | False |
| Local | device | stale | 401 | False |
| Local | device | future | 401 | False |
| Local | device | malformed | 401 | False |
| Local | device | unsupported-version | 400 version | False |
| Local | device | wrong-operation | 401 | False |
| Local | device | replay | 401 | False |
| Local | bootstrap | valid | 200 | True |
| Local | bootstrap | missing | 401 | False |
| Local | bootstrap | changed | 401 | False |
| Local | bootstrap | cross-home | 401 | False |
| Local | bootstrap | stale | 401 | False |
| Local | bootstrap | future | 401 | False |
| Local | bootstrap | malformed | 401 | False |
| Local | bootstrap | unsupported-version | 400 version | False |
| Local | bootstrap | wrong-operation | 401 | False |
| Local | bootstrap | replay | 401 | False |
| ReverseProxy | none | valid | 200 | True |
| ReverseProxy | none | missing | 401 | False |
| ReverseProxy | none | changed | 401 | False |
| ReverseProxy | none | cross-home | 401 | False |
| ReverseProxy | none | stale | 401 | False |
| ReverseProxy | none | future | 401 | False |
| ReverseProxy | none | malformed | 401 | False |
| ReverseProxy | none | unsupported-version | 400 version | False |
| ReverseProxy | none | wrong-operation | 401 | False |
| ReverseProxy | none | replay | 401 | False |
| ReverseProxy | device | valid | 200 | True |
| ReverseProxy | device | missing | 401 | False |
| ReverseProxy | device | changed | 401 | False |
| ReverseProxy | device | cross-home | 401 | False |
| ReverseProxy | device | stale | 401 | False |
| ReverseProxy | device | future | 401 | False |
| ReverseProxy | device | malformed | 401 | False |
| ReverseProxy | device | unsupported-version | 400 version | False |
| ReverseProxy | device | wrong-operation | 401 | False |
| ReverseProxy | device | replay | 401 | False |
| ReverseProxy | bootstrap | valid | 200 | True |
| ReverseProxy | bootstrap | missing | 401 | False |
| ReverseProxy | bootstrap | changed | 401 | False |
| ReverseProxy | bootstrap | cross-home | 401 | False |
| ReverseProxy | bootstrap | stale | 401 | False |
| ReverseProxy | bootstrap | future | 401 | False |
| ReverseProxy | bootstrap | malformed | 401 | False |
| ReverseProxy | bootstrap | unsupported-version | 400 version | False |
| ReverseProxy | bootstrap | wrong-operation | 401 | False |
| ReverseProxy | bootstrap | replay | 401 | False |
| TailscaleServe | none | valid | 200 | True |
| TailscaleServe | none | missing | 401 | False |
| TailscaleServe | none | changed | 401 | False |
| TailscaleServe | none | cross-home | 401 | False |
| TailscaleServe | none | stale | 401 | False |
| TailscaleServe | none | future | 401 | False |
| TailscaleServe | none | malformed | 401 | False |
| TailscaleServe | none | unsupported-version | 400 version | False |
| TailscaleServe | none | wrong-operation | 401 | False |
| TailscaleServe | none | replay | 401 | False |
| TailscaleServe | device | valid | 200 | True |
| TailscaleServe | device | missing | 401 | False |
| TailscaleServe | device | changed | 401 | False |
| TailscaleServe | device | cross-home | 401 | False |
| TailscaleServe | device | stale | 401 | False |
| TailscaleServe | device | future | 401 | False |
| TailscaleServe | device | malformed | 401 | False |
| TailscaleServe | device | unsupported-version | 400 version | False |
| TailscaleServe | device | wrong-operation | 401 | False |
| TailscaleServe | device | replay | 401 | False |
| TailscaleServe | bootstrap | valid | 200 | True |
| TailscaleServe | bootstrap | missing | 401 | False |
| TailscaleServe | bootstrap | changed | 401 | False |
| TailscaleServe | bootstrap | cross-home | 401 | False |
| TailscaleServe | bootstrap | stale | 401 | False |
| TailscaleServe | bootstrap | future | 401 | False |
| TailscaleServe | bootstrap | malformed | 401 | False |
| TailscaleServe | bootstrap | unsupported-version | 400 version | False |
| TailscaleServe | bootstrap | wrong-operation | 401 | False |
| TailscaleServe | bootstrap | replay | 401 | False |
| TailscaleFunnel | none | valid | 200 | True |
| TailscaleFunnel | none | missing | 401 | False |
| TailscaleFunnel | none | changed | 401 | False |
| TailscaleFunnel | none | cross-home | 401 | False |
| TailscaleFunnel | none | stale | 401 | False |
| TailscaleFunnel | none | future | 401 | False |
| TailscaleFunnel | none | malformed | 401 | False |
| TailscaleFunnel | none | unsupported-version | 400 version | False |
| TailscaleFunnel | none | wrong-operation | 401 | False |
| TailscaleFunnel | none | replay | 401 | False |
| TailscaleFunnel | device | valid | 200 | True |
| TailscaleFunnel | device | missing | 401 | False |
| TailscaleFunnel | device | changed | 401 | False |
| TailscaleFunnel | device | cross-home | 401 | False |
| TailscaleFunnel | device | stale | 401 | False |
| TailscaleFunnel | device | future | 401 | False |
| TailscaleFunnel | device | malformed | 401 | False |
| TailscaleFunnel | device | unsupported-version | 400 version | False |
| TailscaleFunnel | device | wrong-operation | 401 | False |
| TailscaleFunnel | device | replay | 401 | False |
| TailscaleFunnel | bootstrap | valid | 200 | True |
| TailscaleFunnel | bootstrap | missing | 401 | False |
| TailscaleFunnel | bootstrap | changed | 401 | False |
| TailscaleFunnel | bootstrap | cross-home | 401 | False |
| TailscaleFunnel | bootstrap | stale | 401 | False |
| TailscaleFunnel | bootstrap | future | 401 | False |
| TailscaleFunnel | bootstrap | malformed | 401 | False |
| TailscaleFunnel | bootstrap | unsupported-version | 400 version | False |
| TailscaleFunnel | bootstrap | wrong-operation | 401 | False |
| TailscaleFunnel | bootstrap | replay | 401 | False |
| CloudflareTunnel | none | valid | 200 | True |
| CloudflareTunnel | none | missing | 401 | False |
| CloudflareTunnel | none | changed | 401 | False |
| CloudflareTunnel | none | cross-home | 401 | False |
| CloudflareTunnel | none | stale | 401 | False |
| CloudflareTunnel | none | future | 401 | False |
| CloudflareTunnel | none | malformed | 401 | False |
| CloudflareTunnel | none | unsupported-version | 400 version | False |
| CloudflareTunnel | none | wrong-operation | 401 | False |
| CloudflareTunnel | none | replay | 401 | False |
| CloudflareTunnel | device | valid | 200 | True |
| CloudflareTunnel | device | missing | 401 | False |
| CloudflareTunnel | device | changed | 401 | False |
| CloudflareTunnel | device | cross-home | 401 | False |
| CloudflareTunnel | device | stale | 401 | False |
| CloudflareTunnel | device | future | 401 | False |
| CloudflareTunnel | device | malformed | 401 | False |
| CloudflareTunnel | device | unsupported-version | 400 version | False |
| CloudflareTunnel | device | wrong-operation | 401 | False |
| CloudflareTunnel | device | replay | 401 | False |
| CloudflareTunnel | bootstrap | valid | 200 | True |
| CloudflareTunnel | bootstrap | missing | 401 | False |
| CloudflareTunnel | bootstrap | changed | 401 | False |
| CloudflareTunnel | bootstrap | cross-home | 401 | False |
| CloudflareTunnel | bootstrap | stale | 401 | False |
| CloudflareTunnel | bootstrap | future | 401 | False |
| CloudflareTunnel | bootstrap | malformed | 401 | False |
| CloudflareTunnel | bootstrap | unsupported-version | 400 version | False |
| CloudflareTunnel | bootstrap | wrong-operation | 401 | False |
| CloudflareTunnel | bootstrap | replay | 401 | False |
