---
name: netclaw-projects
description: "How to create, find, and work with project workspaces. Load when the user references a project, asks to organize work, or you need a sustained workspace."
metadata:
  author: netclaw
  version: "1.2.0"
license: MIT
compatibility: "netclaw >= 0.10.0"
disable-model-invocation: false
---

# Project Workspaces

A **project** is a local git repo with an `AGENTS.md` at its root. Projects
live in your workspaces directory (check `TOOLING.md` for the path).

Projects can be nested at any depth within the workspaces directory. A cloned
engineering repo counts. So does a directory you create for a marketing campaign,
research initiative, or any sustained body of work.

## Finding Existing Projects

Before creating a new project, **always check what already exists**:

```
shell_execute("find <workspaces_path> -name AGENTS.md -maxdepth 4 -type f")
```

Read the `AGENTS.md` of any match to understand its purpose. If a project
already covers the user's request, `cd` into it and work there.

## Creating a New Project

Only create a project when:
- The work is sustained (multi-session, ongoing)
- No existing project covers it
- The user explicitly asks to start one

Steps:
1. Choose a descriptive directory name (kebab-case)
2. `shell_execute("mkdir -p <workspaces_path>/<name> && cd <workspaces_path>/<name> && git init")`
3. Write an `AGENTS.md` with `file_write` describing:
   - What this project is about
   - Goals and constraints
   - Relevant context (target audience, products, tools, etc.)
4. `shell_execute("cd <workspaces_path>/<name> && git add -A && git commit -m 'Initial project setup'")`

The `AGENTS.md` is the project's living context document. Update it as the
project evolves.

## Working in a Project

When you start working on a project, use `set_working_directory` to set the
project directory:

```
set_working_directory(path: "/home/user/workspaces/my-project")
```

This automatically loads the project's identity file (`.netclaw/AGENTS.md`,
`CLAUDE.md`, `AGENTS.md`, or `CONTEXT.md` — first match wins) into the system
prompt. The project context persists across crash/restart, so you don't need to
re-read it manually.

Use the project directory as your working directory for all project-related
file operations. Commit meaningful changes so the project has history.

Team and Personal sessions refresh Git working context at the beginning of a
turn. That snapshot is grounding, not a live view: changes made during the turn
appear in the next turn. A spawned subagent receives its own read-only snapshot
and reports only changes confirmed through its tools. Do not infer that every
dirty file in the shared worktree was changed by that subagent.

## When NOT to Create a Project

One-off tasks don't need a project. If the user asks a quick question, runs a
single search, or needs something done once — just do it in the current session.
Don't create overhead for throwaway work.
