# Install Or Update The Default Task Worker

This procedure applies to an explicit operator request to install or update an agent profile.
Use `agent-coordination` for runtime task workflows. Read its resources through logical skill APIs.
Physical agent-file inspection here is an operator diagnostic, not a runtime skill-root lookup.

New installations seed `task-worker.md` from the canonical release asset when the ordinary destination file is absent.
The seed path preserves existing operator profile files. An upgrade does not automatically replace them.
The worker requests the existing Main role. Existing provider selection and permitted fallback behavior still apply.
Its 120-second timeout is an inactivity bound, not a task lifetime budget.

## Existing Deployments

1. Identify the installed release version and the configured Netclaw home through existing configuration and operator diagnostics.
2. Obtain that release's source asset: `src/Netclaw.Cli/Resources/identity/task-worker.profile.md`.
3. Verify the source revision or release tag. Review the asset before a copy.
4. Resolve the configured agent directory. Its default is the `agents` directory under the configured Netclaw home.
5. Inspect the directory and `task-worker.md` destination under normal file policy.
6. Stop if a path is unresolved, a directory occupies the destination, or any destination component is a symbolic link.
7. If the ordinary destination is absent, copy the reviewed asset to `task-worker.md` through authorized file tools.
8. If a regular file exists, compare it with the asset and preserve its content by default.
9. Apply a replacement only after explicit operator update authority. Preserve any operator edits that the operator wants to retain.
10. Inspect the next profile discovery or load result. Verify the name, mission, Main role, visibility, and inactivity timeout.

There is no dedicated agent-profile installer command.
Do not repeat `netclaw init` as a profile update procedure. Init can regenerate unrelated identity content.
Do not replace `AGENTS.md`, `SOUL.md`, `TOOLING.md`, or other profiles for this operation.
A custom worker profile remains authoritative even if its model role differs from the release default.
If validation fails, report the error. Do not silently substitute a different profile or role.

Positive example: an operator installs the reviewed release asset at an absent ordinary profile path.
Negative example: an agent follows a profile symlink or replaces a custom file without update authority.

## Skill Versions And Rollback

The binary owns the embedded coordination system-skill bundle.
An operator profile remains separate from that managed bundle.
Do not derive system-skill paths to copy a worker profile.
Use the prior binary and its bundle for a binary rollback. Keep operator-owned profiles and playbooks.
Legacy feed publication is a separate operator release action. Normal development pushes do not publish that feed.
