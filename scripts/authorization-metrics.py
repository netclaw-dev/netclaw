#!/usr/bin/env python3
"""Report production size of the tool authorization path.

The report counts lines and declared types for each group of production
files. Each group has an explicit file list and glob patterns. A glob catches
new files in the target layout, for example src/Netclaw.Actors/Authorization/.
A file counts in the first group that claims it. The script skips a listed
file that does not exist and reports it. Test projects are never counted.

The report also gives the total of all production .cs files under src/. Code
that moves from a group to an unlisted path leaves the group total but stays
in the whole-tree total, so a relocation does not look like a reduction.

Counting rules (same as the research script for the 2fe42f1b3 baseline):

- LOC: the number of newline characters in the file (like `wc -l`).
- Types: lines that match TYPE_DECLARATION (like `grep -cE`).

Usage:

    scripts/authorization-metrics.py                   # working tree
    scripts/authorization-metrics.py --rev 2fe42f1b3   # a revision, read with git cat-file
    scripts/authorization-metrics.py --compare 2fe42f1b3 HEAD
    scripts/authorization-metrics.py --rev HEAD --files  # also list each file
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field

TYPE_DECLARATION = re.compile(
    r"^\s*(public|internal|private|protected|file)?\s*"
    r"((sealed|static|abstract|readonly|partial|ref)\s+)*"
    r"(class|record struct|record|interface|enum|struct) [A-Z]\w*")

A = "src/Netclaw.Actors/Tools"
S = "src/Netclaw.Security"
C = "src/Netclaw.Configuration"
SE = "src/Netclaw.Actors/Sessions"
P = "src/Netclaw.Actors/Protocol"
# Planned home of the consolidated authorizer. The globs below route each
# bounded context folder to its group, in any of the production projects that
# hold authorization types. Tools.Abstractions holds the one-time consent value
# because the invocation attempt that carries it lives there.
AUTH = "src/Netclaw.{Actors,Security,Tools.Abstractions}/Authorization"


@dataclass(frozen=True)
class Group:
    name: str
    files: tuple[str, ...]
    globs: tuple[str, ...] = ()


GROUPS: tuple[Group, ...] = (
    Group("1. Pipeline/orchestration", (
        f"{SE}/Pipelines/SessionToolExecutionPipeline.cs", f"{A}/DispatchingToolExecutor.cs",
        f"{A}/ToolAuthorizationDecision.cs", f"{A}/ShellPolicyEvaluation.cs",
        f"{SE}/ToolApprovalState.cs", f"{SE}/IApprovalChannel.cs",
        f"{SE}/ParentSessionApprovalBridge.cs", "src/Netclaw.Tools.Abstractions/IParentApprovalBridge.cs",
    ), (f"{AUTH}/*.cs", f"{AUTH}/Orchestration/**/*.cs")),
    Group("2. Tool-level access policy (audience/mode/options)", (
        f"{A}/ToolAccessPolicy.cs", f"{A}/ToolAudienceProfileResolver.cs",
        f"{A}/FilePathApprovalMatcher.cs", f"{C}/ToolAudienceProfiles.cs",
    ), (f"{AUTH}/Admission/**/*.cs",)),
    Group("3. Shell policy coordination/projection/coverage", (
        f"{A}/ShellPolicyCoordinator.cs", f"{A}/ShellPolicyProjection.cs", f"{A}/ShellPolicyPathFacts.cs",
        f"{A}/ShellPolicyDecisionTrace.cs", f"{A}/ShellApprovalEvidence.cs", f"{A}/BashCausalApprovalIntent.cs",
        f"{A}/BashStaticCompoundApprovalProjection.cs", f"{A}/BashDirectoryScopeProjection.cs",
        f"{A}/ReviewedSafeShellPolicy.cs",
        f"{A}/ShellRedirectPolicyFacts.cs", f"{A}/OneTimeApprovalKeys.cs", f"{C}/SafeVerbList.cs",
    ), (f"{AUTH}/ShellCoverage/**/*.cs",)),
    Group("4. Corrections (temp/project/native)", (
        f"{A}/TemporaryPathCorrectionPolicy.cs", f"{A}/ManagedTemporaryCorrection.cs",
        f"{A}/ManagedTemporaryEnvironment.cs", f"{A}/NativeToolShellCorrectionDetector.cs",
    ), (f"{AUTH}/Advice/**/*.cs",)),
    Group("5. Shell syntax facts / analysis / candidate extraction", (
        f"{S}/IToolApprovalMatcher.cs", f"{S}/ShellCommandAnalysis.cs", f"{S}/ShellApprovalSemantics.cs",
        f"{S}/ShellTokenizer.cs", f"{S}/ShellPathRules.cs", f"{S}/ShellFileSystemTreeAccessPolicy.cs",
        f"{S}/ShellAssignmentDigestFactory.cs", f"{S}/ShellApprovalGrantParser.cs",
    ), (f"{AUTH}/ShellFacts/**/*.cs",)),
    Group("6. Hard deny + protected paths", (
        f"{S}/ShellCommandPolicy.cs", f"{S}/HardDenyRule.cs", f"{S}/HardDenyOverridesLoader.cs",
        f"{S}/ToolPathPolicy.cs",
    ), (f"{AUTH}/Prohibition/**/*.cs",)),
    Group("7. Filesystem authority", (
        f"{A}/PathAccessPolicy.cs",
    ), (f"{AUTH}/Filesystem/**/*.cs",)),
    Group("8. Grant matching/storage", (
        f"{S}/ApprovalPatternMatching.cs", f"{S}/GitRepositoryApprovalScope.cs", f"{S}/IToolApprovalService.cs",
        f"{A}/ToolApprovalActor.cs", f"{A}/ToolApprovalMessages.cs", f"{A}/AkkaToolApprovalService.cs",
        f"{SE}/ApprovalBucketBuilder.cs", f"{C}/ToolApprovalStore.cs", f"{C}/ApprovalEntry.cs",
        f"{C}/ApprovalEntryWireCodec.cs", f"{C}/ApprovalEntryValidation.cs", f"{C}/ApprovalStoreCodec.cs",
        f"{C}/ApprovalStoreModels.cs", f"{C}/ApprovalStoreWireModels.cs", f"{C}/ApprovalStoreFileAccess.cs",
        f"{C}/ApprovalPhrase.cs", f"{C}/ToolApprovalEntryComparer.cs",
    ), (f"{AUTH}/Consent/**/*.cs",)),
    Group("9. Prompt/response protocol + channels", (
        f"{P}/ApprovalOptionKeys.cs", f"{P}/ApprovalOptionKey.cs", f"{P}/ApprovalButtonValueCodec.cs",
        f"{P}/ToolInteractionResponseParser.cs", "src/Netclaw.Channels/ApprovalResponseFlow.cs",
        "src/Netclaw.Channels/PendingApprovalLookup.cs", "src/Netclaw.Channels.Slack/SlackApprovalBlockBuilder.cs",
        "src/Netclaw.Channels.Discord/DiscordApprovalPromptBuilder.cs",
        "src/Netclaw.Channels.Mattermost/MattermostApprovalPromptBuilder.cs",
    ), (f"{AUTH}/ConsentDelivery/**/*.cs",)),
    Group("10. Launch-time re-authorization", (
        f"{A}/ShellProcessLaunch.cs",
    ), (f"{AUTH}/Launch/**/*.cs",)),
    # Catches a file in an Authorization folder that no group above claims, so
    # the total never misses new authorization code.
    Group("11. Other authorization files", (), (f"{AUTH}/**/*.cs",)),
)


def expand_braces(pattern: str) -> list[str]:
    match = re.search(r"\{([^{}]*)\}", pattern)
    if match is None:
        return [pattern]
    head, tail = pattern[:match.start()], pattern[match.end():]
    return [out for option in match.group(1).split(",") for out in expand_braces(head + option + tail)]


def glob_regex(pattern: str) -> re.Pattern[str]:
    parts = []
    index = 0
    while index < len(pattern):
        if pattern.startswith("**/", index):
            parts.append("(?:[^/]+/)*")
            index += 3
        elif pattern[index] == "*":
            parts.append("[^/]*")
            index += 1
        elif pattern[index] == "?":
            parts.append("[^/]")
            index += 1
        else:
            parts.append(re.escape(pattern[index]))
            index += 1
    return re.compile("".join(parts) + r"\Z")


def is_test_project(path: str) -> bool:
    parts = path.split("/")
    return len(parts) > 1 and parts[0] == "src" and "Tests" in parts[1]


def is_production_source(path: str) -> bool:
    return path.startswith("src/") and path.endswith(".cs") and not is_test_project(path)


class Source:
    """Reads files from the working tree or from one git revision."""

    def __init__(self, repo: str, rev: str | None):
        self.repo = repo
        self.rev = rev
        if rev is not None:
            self.commit = git(repo, "rev-parse", "--verify", f"{rev}^{{commit}}").strip()
            listing = git(repo, "ls-tree", "-r", "--name-only", "-z", self.commit, "--", "src")
        else:
            self.commit = None
            listing = git(repo, "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "src")
        self.paths = sorted(
            path for path in listing.split("\0")
            if path and (rev is not None or os.path.isfile(os.path.join(repo, path))))
        self.path_set = set(self.paths)

    @property
    def label(self) -> str:
        return "working tree" if self.rev is None else f"{self.rev} ({self.commit[:12]})"

    def read_many(self, paths: list[str]) -> dict[str, bytes]:
        if self.rev is None:
            contents = {}
            for path in paths:
                with open(os.path.join(self.repo, path), "rb") as handle:
                    contents[path] = handle.read()
            return contents
        # One git process for all blobs; a process per file is slow for the whole tree.
        request = "".join(f"{self.commit}:{path}\n" for path in paths).encode("utf-8")
        output = subprocess.run(
            ["git", "-C", self.repo, "cat-file", "--batch"],
            input=request, capture_output=True, check=True).stdout
        contents = {}
        offset = 0
        for path in paths:
            newline = output.index(b"\n", offset)
            header = output[offset:newline].split()
            if len(header) != 3 or header[1] != b"blob":
                raise SystemExit(f"error: cannot read {self.commit}:{path}: {header!r}")
            size = int(header[2])
            start = newline + 1
            contents[path] = output[start:start + size]
            offset = start + size + 1
        return contents


def git_bytes(repo: str, *args: str) -> bytes:
    completed = subprocess.run(["git", "-C", repo, *args], capture_output=True)
    if completed.returncode != 0:
        raise SystemExit(f"error: git {' '.join(args)} failed: {completed.stderr.decode().strip()}")
    return completed.stdout


def git(repo: str, *args: str) -> str:
    return git_bytes(repo, *args).decode("utf-8")


@dataclass
class FileCount:
    path: str
    loc: int
    types: int


@dataclass
class GroupCount:
    group: Group
    files: list[FileCount] = field(default_factory=list)
    missing: list[str] = field(default_factory=list)

    @property
    def loc(self) -> int:
        return sum(item.loc for item in self.files)

    @property
    def types(self) -> int:
        return sum(item.types for item in self.files)


@dataclass
class Measurement:
    label: str
    groups: list[GroupCount]
    # Every production .cs file under src/. Code that moves out of the groups
    # to an unlisted path still counts here, so a relocation is not a reduction.
    production: list[FileCount]

    @property
    def grouped_loc(self) -> int:
        return sum(group.loc for group in self.groups)

    @property
    def grouped_types(self) -> int:
        return sum(group.types for group in self.groups)

    @property
    def production_loc(self) -> int:
        return sum(item.loc for item in self.production)

    @property
    def production_types(self) -> int:
        return sum(item.types for item in self.production)

    @property
    def missing(self) -> list[tuple[str, str]]:
        return [(group.group.name, path) for group in self.groups for path in group.missing]


def count_file(path: str, content: bytes) -> FileCount:
    text = content.decode("utf-8", errors="replace")
    types = sum(1 for line in text.split("\n") if TYPE_DECLARATION.match(line))
    return FileCount(path, content.count(b"\n"), types)


def measure(source: Source) -> Measurement:
    production_paths = [path for path in source.paths if is_production_source(path)]
    contents = source.read_many(production_paths)
    counts = {path: count_file(path, content) for path, content in contents.items()}

    claimed: set[str] = set()
    groups = []
    for group in GROUPS:
        result = GroupCount(group)
        selected = []
        for path in group.files:
            if path in counts:
                selected.append(path)
            else:
                result.missing.append(path)
        regexes = [glob_regex(p) for pattern in group.globs for p in expand_braces(pattern)]
        selected.extend(path for path in production_paths if any(regex.match(path) for regex in regexes))
        for path in selected:
            if path not in claimed:
                claimed.add(path)
                result.files.append(counts[path])
        groups.append(result)
    return Measurement(source.label, groups, [counts[path] for path in production_paths])


def render(measurement: Measurement, show_files: bool) -> str:
    results = measurement.groups
    width = max(len(r.group.name) for r in results)
    lines = [f"Authorization production metrics: {measurement.label}", ""]
    lines.append(f"{'Group'.ljust(width)}  {'LOC':>7}  {'Types':>5}  {'Files':>5}")
    for result in results:
        lines.append(
            f"{result.group.name.ljust(width)}  {result.loc:>7,}  {result.types:>5}  {len(result.files):>5}")
        if show_files:
            for item in result.files:
                lines.append(f"    {item.path}  {item.loc} LOC  {item.types} types")
    grouped_files = sum(len(r.files) for r in results)
    lines.append(
        f"{'TOTAL'.ljust(width)}  {measurement.grouped_loc:>7,}  {measurement.grouped_types:>5}  "
        f"{grouped_files:>5}")
    lines.append("")
    lines.append(
        f"{'All production .cs under src/'.ljust(width)}  {measurement.production_loc:>7,}  "
        f"{measurement.production_types:>5}  {len(measurement.production):>5}")
    if measurement.missing:
        lines.append("")
        lines.append(f"Listed files not found ({len(measurement.missing)}; skipped):")
        lines.extend(f"    {path}  [{name}]" for name, path in measurement.missing)
    return "\n".join(lines)


def render_compare(base: Measurement, head: Measurement) -> str:
    width = max(len(r.group.name) for r in base.groups)
    lines = [f"Authorization production metrics: {base.label} -> {head.label}", ""]
    lines.append(
        f"{'Group'.ljust(width)}  {'Base LOC':>9}  {'Head LOC':>9}  {'Delta':>8}  "
        f"{'Base T':>6}  {'Head T':>6}  {'Delta':>6}")

    def row(name, base_loc, head_loc, base_types, head_types):
        return (f"{name.ljust(width)}  {base_loc:>9,}  {head_loc:>9,}  {head_loc - base_loc:>+8,}  "
                f"{base_types:>6}  {head_types:>6}  {head_types - base_types:>+6}")

    for before, after in zip(base.groups, head.groups):
        lines.append(row(before.group.name, before.loc, after.loc, before.types, after.types))
    lines.append(row("TOTAL", base.grouped_loc, head.grouped_loc, base.grouped_types, head.grouped_types))
    lines.append("")
    lines.append(row(
        "All production .cs under src/", base.production_loc, head.production_loc,
        base.production_types, head.production_types))
    lines.append(row(
        "Production outside the groups",
        base.production_loc - base.grouped_loc, head.production_loc - head.grouped_loc,
        base.production_types - base.grouped_types, head.production_types - head.grouped_types))
    lines.append("")
    lines.append(
        "Read the group delta with the whole-tree delta. Code that moves to an unlisted "
        "path leaves the groups but stays in the whole tree.")
    for measurement in (base, head):
        if measurement.missing:
            lines.append("")
            lines.append(
                f"WARNING: {len(measurement.missing)} listed file(s) not found at {measurement.label}. "
                "A missing file lowers its group total:")
            lines.extend(f"    {path}  [{name}]" for name, path in measurement.missing)
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Report production LOC and declared types for the tool authorization path.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__)
    parser.add_argument("--repo", default=".", help="repository root (default: current directory)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--rev", help="read files at this git revision (no checkout)")
    mode.add_argument("--compare", nargs=2, metavar=("BASE_REV", "HEAD_REV"),
                      help="print both revisions and the delta per group")
    parser.add_argument("--files", action="store_true", help="list each counted file")
    args = parser.parse_args(argv)
    repo = os.path.abspath(args.repo)

    if args.compare:
        base = measure(Source(repo, args.compare[0]))
        head = measure(Source(repo, args.compare[1]))
        print(render(base, args.files))
        print()
        print(render(head, args.files))
        print()
        print(render_compare(base, head))
        return 0

    print(render(measure(Source(repo, args.rev)), args.files))
    return 0


if __name__ == "__main__":
    sys.exit(main())
