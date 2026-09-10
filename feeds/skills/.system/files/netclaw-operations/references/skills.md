# Skill Management


## Skill Management


The `netclaw skill` CLI manages skills and skill sources. `netclaw skill list`
needs the running daemon, because it lists the daemon's live registry — the
only view that includes dynamic MCP prompt skills. `netclaw skill sync` also
needs the daemon. Other subcommands are offline.

During an agent session, use `skill_load(name)` to activate guidance and
`skill_read_resource(name, path)` for bundled files. Skill origin and physical
location are intentionally hidden behind those logical tools. Use the CLI path
commands below only for explicit operator inspection and diagnostics.

| Command | What it does |
|---------|--------------|
| `netclaw skill list` | List all discovered skills with source, version, status |
| `netclaw skill sync` | Run one external source sync pass and report each source result |
| `netclaw skill show <name>` | Show skill metadata and full content |
| `netclaw skill validate <path>` | Validate a SKILL.md file's frontmatter format |
| `netclaw skill remove <name>` | Remove a native skill (refuses system/external) |
| `netclaw skill issues` | Show only scanner issues (rejected items with reasons) |
| `netclaw skill search <query>` | Search skills by name or description |

### External skill sources

Register additional skill directories (e.g. `~/.claude/skills/`):

| Command | What it does |
|---------|--------------|
| `netclaw skill source list` | Show configured external sources |
| `netclaw skill source add <name> --well-known claude-code` | Add Claude Code skills |
| `netclaw skill source add <name> --path /shared/skills` | Add a custom directory |
| `netclaw skill source remove <name>` | Remove a source |
| `netclaw skill source enable <name>` | Enable a disabled source |
| `netclaw skill source disable <name>` | Disable without removing |

The daemon restores its system skills from its binary before its first scan.
If this restore fails, confirm that Netclaw owns the skills directory and can
write to its parent. If the error reports a reparse point, remove it from the
`.system` path. Then restart the daemon. Private server feeds remain independent
skill sources.
It rebuilds one complete inventory across system, native, managed-feed, and
external sources after syncs and supported mutations. Native skills take
precedence over managed feeds, which take precedence over external sources.
No restart is needed for supported mutations.

`netclaw skill sync` uses the daemon's configured sources. It cannot add a
source or write configuration. The command waits for the shared daemon pass.
If its wait is canceled, the daemon can still finish that pass.
