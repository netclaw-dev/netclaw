## 1. Policy lists

- [x] 1.1 Narrow the daemon read-deny and shell lists to secrets, keys, the database, process control, and the tooling shadow; verify `DaemonToolPathPolicyFactoryTests` pass
- [x] 1.2 Replace the `.netclaw/config` text hint with the guarded-directory rule; verify the `ToolPathPolicyTests` text cases pass

## 2. Shell read relaxation

- [x] 2.1 Add the read-only program list and give a read-only occurrence `Read` protection for a write-protected path; verify `ShellConfigReadTests` pass
- [x] 2.2 Keep a directory operand that holds a read-denied path, a glob, an unbounded value, and a plain entry word on `Write` protection; verify the secret-read and write cases in `ShellConfigReadTests` stay denied

## 3. Gates and docs

- [x] 3.1 Add the `shell-config-read` focused mutation gate; verify `./scripts/run-shell-config-read-mutations.sh` kills every mutant
- [x] 3.2 Update the runbooks, the glossary, and the `netclaw-operations` skill; verify the docs name only `secrets.json` and the keys as read-denied config
