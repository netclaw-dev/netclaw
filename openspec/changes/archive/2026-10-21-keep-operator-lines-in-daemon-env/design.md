## Context

The daemon environment file holds the `PATH` line that netclaw writes. An operator can add other lines to it. See proposal.md for the reason.

## Decisions

- A line is a `PATH` line when its key, the text before the first `=`, is `PATH` once trimmed. This matches how systemd reads the key. `export PATH=/x` is another key, so it stays.
- The first `PATH` line changes in place. A later `PATH` line goes. When none exists, `PATH=` goes at the end.
- The commands never read the value of another line.

## Risks / Trade-offs

- The file holds plain text. A credential in an operator line needs `chmod 600`. The commands never open the file mode more than it was, but they do not make a new file private.
- `netclaw daemon uninstall` still deletes the file, including the operator lines.
