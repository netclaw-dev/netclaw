# Skill Management


## Skill Management


The `netclaw skill` CLI manages skills and skill sources. All subcommands
are offline — no daemon required.

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

The daemon's `SkillDirectoryWatcherService` automatically rescans all skill
directories (native + external) when files change on disk. No restart needed.
