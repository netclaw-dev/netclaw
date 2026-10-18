#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$repo_root/src/Netclaw.Actors/Tools/PathAccessPolicy.cs"
temp_source_file="$repo_root/src/Netclaw.Actors/Tools/ManagedTemporaryEnvironment.cs"
test_project="$repo_root/src/Netclaw.Actors.MutationTests"
output_path="${1:-$repo_root/artifacts/stryker/path-access-policy}"
if [[ "$output_path" != /* ]]; then
  output_path="$repo_root/$output_path"
fi

read -r span_start span_end < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "if (context.Audience == TrustAudience.Personal)";
    $end_marker = "roots.AddRange(_sessionRoots);";
    $start = index($_, $start_marker);
    die "The start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end_start = index($_, $end_marker, $start);
    die "The end marker is missing or duplicated.\n"
      if $end_start < 0 || index($_, $end_marker, $end_start + 1) >= 0;
    print "$start ", $end_start + length($end_marker), "\n";
  ' "$source_file"
)

# The read branch of the reviewed-safe path check: only a host path of the
# shell's own style can use the read authority of the audience. Attended and
# unattended runs use the same branch (D2).
read -r read_start read_end < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "=> CanonicalPath.IsHostPathStyle(pathStyle)";
    $end_marker = "is PathAccessDecision.Allowed;";
    $start = index($_, $start_marker);
    die "The read-branch start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end_start = index($_, $end_marker, $start);
    die "The read-branch end marker is missing.\n" if $end_start < 0;
    print "$start ", $end_start + length($end_marker), "\n";
  ' "$source_file"
)

# Issue #2554, Fix A: the effective profile of a Personal read or attach. The span holds the
# Personal audience check, the Read and Attach operation check, the write-mode check, and the
# root union.
read -r reach_start reach_end < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "if (audience != TrustAudience.Personal";
    $end_marker = "_ => access";
    $start = index($_, $start_marker);
    die "The reach start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end_start = index($_, $end_marker, $start);
    die "The reach end marker is missing or duplicated.\n"
      if $end_start < 0 || index($_, $end_marker, $end_start + 1) >= 0;
    print "$start ", $end_start + length($end_marker), "\n";
  ' "$source_file"
)

# Issue #2554, Fix B: the key folder of the managed temporary location is a session root. The
# shared base is not. The span holds the line that adds the root.
read -r key_start key_end < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "if (context.SessionStorage is { } sessionStorage)";
    $end_marker = "roots.Add(sessionStorage.ManagedTemporary.StorageRoot.Value);\n        }";
    $start = index($_, $start_marker);
    die "The key-root start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end_start = index($_, $end_marker, $start);
    die "The key-root end marker is missing or duplicated.\n"
      if $end_start < 0 || index($_, $end_marker, $end_start + 1) >= 0;
    print "$start ", $end_start + length($end_marker), "\n";
  ' "$source_file"
)

# Issue #2554, Fix B: the folder checks of the managed temporary base and key folder (a missing
# folder, a link, a foreign owner, a group or other bit).
read -r folder_start folder_end < <(
  perl -Mopen=:std,:encoding\(UTF-8\) -0777 -ne '
    $start_marker = "if (!inspector.TryInspect(folder, out var facts))";
    $end_marker = "// the folder passed every check";
    $start = index($_, $start_marker);
    die "The folder-check start marker is missing or duplicated.\n"
      if $start < 0 || index($_, $start_marker, $start + 1) >= 0;
    $end_start = index($_, $end_marker, $start);
    die "The folder-check end marker is missing or duplicated.\n"
      if $end_start < 0 || index($_, $end_marker, $end_start + 1) >= 0;
    print "$start ", $end_start + length($end_marker), "\n";
  ' "$temp_source_file"
)

(
  cd "$test_project"
  dotnet stryker \
    --config-file stryker-config.json \
    --mutate "Tools/PathAccessPolicy.cs{$span_start..$span_end}" \
    --mutate "Tools/PathAccessPolicy.cs{$read_start..$read_end}" \
    --mutate "Tools/PathAccessPolicy.cs{$reach_start..$reach_end}" \
    --mutate "Tools/PathAccessPolicy.cs{$key_start..$key_end}" \
    --mutate "Tools/ManagedTemporaryEnvironment.cs{$folder_start..$folder_end}" \
    --output "$output_path" \
    --skip-version-check
)

report="$output_path/reports/mutation-report.json"
tested_count="$(
  jq '[.files[].mutants[] | select(.status != "Ignored" and .status != "CompileError")] | length' "$report"
)"
killed_count="$(jq '[.files[].mutants[] | select(.status == "Killed")] | length' "$report")"

if [[ "$tested_count" -ne 24 || "$killed_count" -ne 24 ]]; then
  echo "Expected 24 killed path-access mutants. Found $killed_count killed from $tested_count tested." >&2
  exit 1
fi
