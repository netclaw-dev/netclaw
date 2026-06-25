# Secret Management


## Secret Management


Secrets live in `~/.netclaw/config/secrets.json`; never print raw values in a
conversation, issue, PR, or log summary. Use the CLI instead of direct edits:

```bash
netclaw secrets set Discord:BotToken <replacement>
netclaw secrets set Slack.BotToken <replacement>
```

Rules:

- `.` and `:` are both accepted as path delimiters; prefer the documented dotted
  form unless the operator provides a configuration-style colon path.
- `netclaw secrets add` is an alias for `set` and overwrites the same effective
  path.
- Re-running `netclaw init` on an existing install opens an action menu
  (`Redo identity setup`, `Open configuration editor`, `Start over from
  scratch`, `Cancel`) rather than re-walking setup. Update individual secrets
  with `netclaw secrets set` or the relevant `netclaw config` editor.
- If a channel reports a 401 or invalid-token error, rotate the relevant secret
  and restart the daemon so the channel reloads config.
