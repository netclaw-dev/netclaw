# Skill Management


## Skill Management


The `netclaw skill` CLI manages skills and skill sources. All subcommands
are offline — no daemon required.

During an agent session, use `skill_load(name)` to activate guidance and
`skill_read_resource(name, path)` for bundled files. Skill origin and physical
location are intentionally hidden behind those logical tools. Use the CLI path
commands below only for explicit operator inspection and diagnostics.

| Command | What it does |
|---------|--------------|
| `netclaw skill list` | List all discovered skills with source, version, status |
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

The daemon automatically rebuilds one complete inventory across native,
managed-feed, and external sources after syncs and supported mutations. Native
skills take precedence over managed feeds, which take precedence over external
sources. No restart is needed.
