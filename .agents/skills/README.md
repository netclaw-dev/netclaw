# Accordant skills

These nine skills come from [`microsoft/accordant`](https://github.com/microsoft/accordant/tree/0b2c0d4272370b231f6c9c1ec3fd9602e3fe44f7/agent/skills).
The source revision is `0b2c0d4272370b231f6c9c1ec3fd9602e3fe44f7`.
The files retain the upstream text after removal of trailing whitespace.
[LICENSE.accordant](LICENSE.accordant) retains the exact upstream MIT notice.

Codex reads each `.agents/skills/accordant-*/SKILL.md` file.
Claude Code reads each relative link at `.claude/skills/accordant-*`.
OpenCode reads the `.agents/skills/accordant-*` paths directly.
The Claude links point to these same files, so the skill text has one copy.

To refresh the skills, select and review a new Accordant revision.
Use the Codex `skill-installer` helper with `--repo microsoft/accordant`, `--ref <revision>`, and all nine `agent/skills/<topic>` paths.
Set `--dest` to a new temporary directory.
Check each file name, frontmatter name, relative link, and license notice.
Remove trailing whitespace from the nine skill files.
Then replace the nine canonical `SKILL.md` files and the license notice.
Keep the Claude links relative to the repository root.
Verify the paths with `git status --short` and `git diff --check`.

The filesystem checks confirm the installed files and links.
They do not confirm that an active agent session refreshed its skill catalog.
