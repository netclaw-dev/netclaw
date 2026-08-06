fix: close symlinked-dir and sqlite-sidecar read-deny gaps; clamp set_working_directory in Mode.All (#1724)

Second adversarial review found:
1. IsReadDenied bypass via symlinked INTERMEDIATE directory (ln -s config
   /tmp/x then read /tmp/x/netclaw.json). IsDeniedAgainst now resolves
   intermediate symlinks segment-by-segment via TryResolveSymlinksInPath,
   mirroring the shell scanner. attach_file re-checks the deny against the
   resolved path as defense-in-depth.
2. set_working_directory opt-out was inert for the default Mode.All
   Personal profile — the Mode.All branch fired before the opt-out flag was
   consulted. The branch now clamps set_working_directory to the autonomous
   zone in every mode.
3. Prefix-collision gap: sqlite sidecar files (wal/shm/journal) held raw
   page data (secrets) but path-boundary matching allowed reads while shell
   denied them. Program.cs shell indicator list now includes the sidecars;
   fixture mirrors production.
4. CredentialReadDenied message now covers control-plane state, not just
   credentials/keys; ToolPathPolicy remarks updated to match production
   (directory-scoped shell entries are intentional).
5. Spec: attach_file and set_working_directory requirements added.

Tests: symlinked-dir read-deny regression, Mode.All working-dir clamp,
Roots-mode attach branch (was untested), sidecar read-deny cases.
