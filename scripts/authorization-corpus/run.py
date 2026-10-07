#!/usr/bin/env python3
"""Cross-revision authorization corpus differential.

Runs the same corpus of tool calls through the production authorization path
of two revisions and compares every decision. Each revision builds in its own
disposable git worktree. The probe runs with a private temporary directory, so
no decision reads the shared /tmp. The probe decides the shell inputs in
parallel lanes (--jobs) and writes the same output for each lane count.

Usage (from the repository root):

    python3 scripts/authorization-corpus/run.py --base upstream/dev
    python3 scripts/authorization-corpus/run.py --base upstream/dev --head HEAD --quick
    python3 scripts/authorization-corpus/run.py --base upstream/dev --jobs 1

The script exits with 0 when the two revisions give the same decision for
every input, and with 1 when a decision differs. After the compare, it deletes
the head output and the disposable worktrees unless --keep is set. The base
output stays as the cache. The report lists the time of each phase. See
TOOLING.md, section "Authorization corpus differential".
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
import warnings
from pathlib import Path

HERE = Path(__file__).resolve().parent
PROBE_DIR = HERE / "probe"
PROBE_TEST_DIRECTORY = Path("src/Netclaw.Actors.Tests/Tools")
TEST_PROJECT = Path("src/Netclaw.Actors.Tests/Netclaw.Actors.Tests.csproj")
PROBE_CLASS = "Netclaw.Actors.Tests.Tools.AuthorizationCorpusProbe"

# The corpus source revision. A fixed revision keeps the corpus identical for
# every slice, so each run repeats the same proof. #2290 merged here.
DEFAULT_CORPUS_REVISION = "4244eaed5"
CORPUS_SOURCES = ("src/Netclaw.Actors.Tests/", "src/Netclaw.Security.Tests/")

# Each literal also runs after these compound prefixes. They exercise the cd
# directory proof, a causal list, an external directory, and the temporary root.
PREFIXES = (
    "cd {X} && gh api x > log 2>&1; ",
    "cd {X} && make; cd {P}/sub && inspect; ",
    "cd {P}/sub && git fetch; ",
    "cd {T} && inspect; ",
)

LITERAL = re.compile(r'(?<![@$\w])"((?:\\.|[^"\\\n])*)"')
# The literal /tmp names the shared platform temporary root. The probe runs with
# a private temporary root ({T}), so a decision never reads the shared /tmp.
SHARED_TMP = re.compile(r"(?<![\w./-])/tmp(?=$|[/\s\"';&|)<>])")


def git(repository: Path, *args: str, binary: bool = False) -> str | bytes:
    result = subprocess.run(["git", *args], cwd=repository, check=True, capture_output=True)
    return result.stdout if binary else result.stdout.decode("utf-8")


def resolve(repository: Path, revision: str) -> str:
    return str(git(repository, "rev-parse", "--verify", f"{revision}^{{commit}}")).strip()


def unescape(literal: str) -> str:
    # A C# escape that Python does not know (for example \l) stays as written.
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", DeprecationWarning)
        try:
            return bytes(literal, "utf-8").decode("unicode_escape").encode("latin-1").decode("utf-8")
        except (UnicodeDecodeError, UnicodeEncodeError):
            return literal


def build_corpus(repository: Path, revision: str) -> list[str]:
    """Takes every string literal of the authorization test sources of one revision.

    The corpus keeps prose, paths, and fragments on purpose: the parser and the
    policy must give a stable decision for any text that a model can send.
    """
    names = str(git(repository, "ls-tree", "-r", "--name-only", revision, "--", *CORPUS_SOURCES)).splitlines()
    literals: set[str] = set()
    for name in sorted(name for name in names if name.endswith(".cs")):
        text = bytes(git(repository, "show", f"{revision}:{name}", binary=True)).decode("utf-8-sig")
        for match in LITERAL.finditer(text):
            value = unescape(match.group(1))
            if 1 <= len(value) <= 300:
                literals.add(value)

    extra = (HERE / "extra-commands.txt").read_text(encoding="utf-8").splitlines()
    literals.update(line for line in extra if line.strip())
    base = sorted({SHARED_TMP.sub("{T}", value) for value in literals})
    corpus = set(base)
    for prefix in PREFIXES:
        corpus.update(prefix + value for value in base)
    return sorted(corpus)


def probe_digest(adapter: str, corpus_path: Path) -> str:
    digest = hashlib.sha256()
    for path in (PROBE_DIR / "AuthorizationCorpusProbe.cs", PROBE_DIR / f"{adapter.capitalize()}Adapter.cs", corpus_path):
        digest.update(path.read_bytes())
    return digest.hexdigest()[:12]


def detect_adapter(worktree: Path, requested: str) -> str:
    if requested != "auto":
        return requested
    executor = (worktree / "src/Netclaw.Actors/Tools/DispatchingToolExecutor.cs").read_text(encoding="utf-8")
    return "gate" if "Task<ToolAuthorizationResult> EvaluateAuthorizationResultAsync" in executor else "authorizer"


def run_revision(
    repository: Path,
    out: Path,
    revision: str,
    adapter_request: str,
    corpus_path: Path,
    states: str | None,
    jobs: int,
    keep: bool,
    timing: dict[str, float | None],
    label: str,
    fresh: bool = False,
) -> Path:
    sha = resolve(repository, revision)
    # The probe reads only src/. Two revisions with the same src/ tree give the
    # same decisions, so they share one output.
    tree = str(git(repository, "rev-parse", f"{sha}:src")).strip()
    worktree = out / "worktrees" / sha[:12]
    if worktree.exists():
        subprocess.run(["git", "worktree", "unlock", str(worktree)], cwd=repository, capture_output=True)
        git(repository, "worktree", "remove", "--force", str(worktree))
    git(repository, "worktree", "add", "--detach", str(worktree), sha)
    # A locked worktree survives "git worktree prune" in other checkouts during a long run.
    git(repository, "worktree", "lock", "--reason", "authorization corpus run", str(worktree))
    partial: Path | None = None
    try:
        adapter = detect_adapter(worktree, adapter_request)
        key = f"src-{tree[:12]}-{adapter}-{probe_digest(adapter, corpus_path)}{'-' + hashlib.sha256(states.encode()).hexdigest()[:8] if states else ''}"
        # A fresh probe writes its own file, so that two probes of one revision stay apart.
        result = out / f"{key}{'-fresh' if fresh else ''}.tsv"
        if result.exists() and not fresh:
            print(f"[{revision}] {sha[:12]} reuse {result.name}")
            timing[f"build {label}"] = None
            timing[f"probe {label}"] = None
            return result

        print(f"[{revision}] {sha[:12]} src tree {tree[:12]}")
        tests = worktree / PROBE_TEST_DIRECTORY
        shutil.copy(PROBE_DIR / "AuthorizationCorpusProbe.cs", tests / "AuthorizationCorpusProbe.cs")
        shutil.copy(PROBE_DIR / f"{adapter.capitalize()}Adapter.cs", tests / "AuthorizationCorpusProbeAdapter.cs")

        print(f"[{revision}] {sha[:12]} adapter={adapter}: build")
        build_started = time.monotonic()
        subprocess.run(
            ["dotnet", "build", str(TEST_PROJECT), "-c", "Debug", "-v", "quiet", "-nologo"],
            cwd=worktree,
            check=True)
        timing[f"build {label}"] = time.monotonic() - build_started
        print(f"[{revision}] build: {format_seconds(timing[f'build {label}'])}")

        temporary = out / "run" / sha[:12] / "tmp"
        shutil.rmtree(temporary.parent, ignore_errors=True)
        temporary.mkdir(parents=True)
        partial = result.with_suffix(".partial")
        environment = dict(os.environ)
        environment.update({
            "NETCLAW_CORPUS_IN": str(corpus_path),
            "NETCLAW_CORPUS_OUT": str(partial),
            "NETCLAW_CORPUS_REPOSITORY": str(worktree),
            "NETCLAW_CORPUS_PARALLELISM": str(jobs),
            # A private temporary root: the harness directories, the platform
            # temporary root of the policy, and every {T} input live here.
            "TMPDIR": str(temporary),
            "TMP": str(temporary),
            "TEMP": str(temporary),
        })
        if states:
            environment["NETCLAW_CORPUS_STATES"] = states
        print(f"[{revision}] probe with {jobs} lane(s) (this takes several minutes)")
        log = result.with_suffix(".log")
        started = time.monotonic()
        with log.open("w", encoding="utf-8") as handle:
            process = subprocess.Popen(
                ["dotnet", "test", str(TEST_PROJECT), "-c", "Debug", "--no-build", "--filter", f"FullyQualifiedName~{PROBE_CLASS}"],
                cwd=worktree,
                env=environment,
                stdout=handle,
                stderr=subprocess.STDOUT)
            progress = Progress(partial)
            try:
                while True:
                    try:
                        process.wait(timeout=PROGRESS_SECONDS)
                        break
                    except subprocess.TimeoutExpired:
                        print(f"[{revision}] probe: {progress.update()} after {int(time.monotonic() - started)} s")
            except BaseException:
                # The same cleanup as subprocess.run: an interrupted script stops its probe.
                process.kill()
                process.wait()
                raise
        if process.returncode != 0 or "Total:     1" not in log.read_text(encoding="utf-8"):
            raise SystemExit(f"[{revision}] the probe failed (exit status {process.returncode}). See {log}.")
        timing[f"probe {label}"] = time.monotonic() - started
        print(f"[{revision}] probe: {format_seconds(timing[f'probe {label}'])}")
        partial.replace(result)
        shutil.rmtree(temporary.parent, ignore_errors=True)
        return result
    finally:
        git(repository, "worktree", "unlock", str(worktree))
        if not keep:
            # The worktree holds the build output (bin and obj). A failed or
            # interrupted probe leaves a partial output of hundreds of MB.
            git(repository, "worktree", "remove", "--force", str(worktree))
            shutil.rmtree(out / "run" / sha[:12], ignore_errors=True)
            if partial is not None:
                partial.unlink(missing_ok=True)


PROGRESS_SECONDS = 60


class Progress:
    """Counts the complete lines of the probe output while the probe writes it."""

    def __init__(self, path: Path):
        self.path = path
        self.offset = 0
        self.rows = 0
        self.state = ""

    def update(self) -> str:
        if self.path.exists():
            with self.path.open("rb") as handle:
                handle.seek(self.offset)
                chunk = handle.read()
            end = chunk.rfind(b"\n") + 1
            if end > 0:
                self.offset += end
                self.rows += chunk.count(b"\n", 0, end)
                last = chunk[:end - 1].rsplit(b"\n", 1)[-1]
                self.state = last.split(b"\t", 1)[0].decode("utf-8", "replace")
        return f"{self.rows:,} lines, current state {self.state or '<none>'}"


def format_seconds(seconds: float | None) -> str:
    if seconds is None:
        return "cache hit (not run)"
    whole = int(round(seconds))
    return f"{whole // 3600}h {whole % 3600 // 60:02d}m {whole % 60:02d}s ({whole} s)" if whole >= 3600 else f"{whole // 60}m {whole % 60:02d}s ({whole} s)"


# The probe escapes a backslash as two characters, so a Windows separator is
# one or two backslashes in the output text.
SEPARATOR = r"(?:/|\\{1,2})"
# What may follow a path in the output text: a separator, a field mark, a
# space, a quote, or the end.
PATH_END = r"""(?=[/|;\\\s"')]|$)"""
# The probe replaces the revision folder name with this text.
REVISION_FOLDER = re.escape("{REVISION}")


def path_pattern(path: Path) -> str:
    """Matches a path in the probe output with either separator."""
    parts = [re.escape(part) for part in re.split(r"[/\\]+", str(path)) if part]
    return SEPARATOR + SEPARATOR.join(parts)


def make_normalizer(out: Path):
    """Returns a function that replaces the run-specific text of a decision line.

    A decision line can show a revision folder when an input path uses "..".
    The two revisions have different folders, so each folder gets a fixed token:

    - <out>/worktrees/{REVISION}  becomes {REVISION_ROOT}
    - <out>/run/{REVISION}        becomes {RUN_ROOT}  (the parent of the private temporary root)
    - <out>                       becomes {WORK_ROOT}

    The probe replaces the paths that it knows and the revision folder name
    (see Cleaner in the probe). A ".." path reaches the folders above them,
    which the probe does not know by path.
    """
    work = path_pattern(out)
    rules = [
        (re.compile(f"{work}{SEPARATOR}worktrees{SEPARATOR}{REVISION_FOLDER}{PATH_END}"), "{REVISION_ROOT}"),
        (re.compile(f"{work}{SEPARATOR}run{SEPARATOR}{REVISION_FOLDER}{PATH_END}"), "{RUN_ROOT}"),
        (re.compile(f"{work}{PATH_END}"), "{WORK_ROOT}"),
    ]

    def normalize(text: str) -> str:
        for rule, token in rules:
            text = rule.sub(token, text)
        return text

    return normalize


def load(path: Path, normalize) -> tuple[str, dict[tuple[str, str], tuple[str, str]]]:
    """Returns the adapter name and, for each decision, its outcome and a digest of its text."""
    adapter = "unknown"
    rows: dict[tuple[str, str], tuple[str, str]] = {}
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("#adapter\t"):
                adapter = line.rstrip("\n").split("\t", 1)[1]
                continue
            state, index, text = line.rstrip("\n").split("\t", 2)
            outcome = text.split("\\n", 1)[0].removeprefix("outcome=")
            rows[(state, index)] = (outcome, hashlib.sha1(normalize(text).encode("utf-8")).hexdigest())
    return adapter, rows


def text_of(path: Path, wanted: set[tuple[str, str]], normalize) -> dict[tuple[str, str], str]:
    found: dict[tuple[str, str], str] = {}
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("#"):
                continue
            state, index, text = line.rstrip("\n").split("\t", 2)
            if (state, index) in wanted:
                found[(state, index)] = normalize(text)
    return found


def compare(base: Path, head: Path, corpus: list[str], report: Path, examples: int, out: Path, timing: Timing) -> int:
    normalize = make_normalizer(out)
    started = time.monotonic()
    base_adapter, before = load(base, normalize)
    head_adapter, after = load(head, normalize)
    keys = sorted(set(before) | set(after))
    outcomes = collections.Counter(after[key][0] for key in after)
    transitions: collections.Counter[tuple[str, str, str]] = collections.Counter()
    differing = []
    for key in keys:
        old, new = before.get(key), after.get(key)
        if old is not None and new is not None and old[1] == new[1]:
            continue
        differing.append(key)
        family = key[0].split("-", 1)[0]
        transitions[(family, old[0] if old else "<missing>", new[0] if new else "<missing>")] += 1

    lines = [
        f"base: {base.name} (adapter {base_adapter})",
        f"head: {head.name} (adapter {head_adapter})",
        f"corpus: {len(corpus)} shell inputs",
        f"comparisons: base {len(before)}, head {len(after)}",
        "head outcomes: " + ", ".join(f"{name} {count}" for name, count in sorted(outcomes.items())),
        f"differences: {len(differing)}",
    ]
    for (family, old, new), count in sorted(transitions.items(), key=lambda item: -item[1]):
        lines.append(f"  {family}: {old} -> {new}: {count}")

    shown = differing[:examples]
    base_text, head_text = text_of(base, set(shown), normalize), text_of(head, set(shown), normalize)
    for state, index in shown:
        number = int(index.split("+", 1)[0])
        subject = corpus[number] if state.startswith(("bash", "pwsh")) and number < len(corpus) else f"tool input {number}"
        lines.append(f"--- {state} {index}: {subject!r}")
        old_fields = base_text.get((state, index), "<missing>").split("\\n")
        new_fields = head_text.get((state, index), "<missing>").split("\\n")
        for field in sorted(set(old_fields) ^ set(new_fields), key=lambda value: (value not in old_fields, value)):
            lines.append(f"  {'-' if field in old_fields else '+'} {field[:400]}")

    timing.phases["compare"] = time.monotonic() - started
    timing.phases["total"] = time.monotonic() - timing.started
    block = timing.lines()
    # The report is the durable output. The timing block comes before the examples.
    head_lines = lines[: 6 + len(transitions)]
    report.write_text("\n".join(head_lines + block + lines[6 + len(transitions):]) + "\n", encoding="utf-8")
    print("\n".join(head_lines))
    print("\n".join(block))
    print(f"report: {report}")
    return 1 if differing else 0


class Timing:
    """Wall-clock time of each phase, with the lane and CPU count of the run."""

    def __init__(self, jobs: int):
        self.started = time.monotonic()
        self.jobs = jobs
        self.phases: dict[str, float | None] = {}

    def lines(self) -> list[str]:
        load_average = os.getloadavg()[0] if hasattr(os, "getloadavg") else None
        rows = [
            "timing:",
            f"  lanes: {self.jobs}",
            f"  cpus: {os.cpu_count()}",
        ]
        if load_average is not None:
            rows.append(f"  load average at the end (1 min): {load_average:.1f}")
        for name in ("corpus", "build base", "probe base", "build head", "probe head", "compare", "total"):
            if name in self.phases:
                rows.append(f"  {name}: {format_seconds(self.phases[name])}")
        return rows


def directory_size(path: Path) -> int:
    return sum(item.stat().st_size for item in path.rglob("*") if item.is_file()) if path.is_dir() else 0


def clean_up(out: Path, base: Path, head: Path, keep: bool, prune_cache: bool) -> None:
    """Makes the report the durable output.

    The base output stays: it is the cache that the next run reuses. The head
    output goes unless it is the base output (a self-compare). --prune-cache
    also removes every other output in the work directory.
    """
    for folder in ("worktrees", "run"):
        try:
            (out / folder).rmdir()  # only when empty: no disposable folder is left
        except OSError:
            pass
    if keep:
        print(f"kept: {head.name} and the worktrees (--keep)")
    else:
        if head != base:
            head.unlink(missing_ok=True)
            print(f"deleted: {head.name}")
        if prune_cache:
            for other in out.glob("src-*.tsv"):
                if other != base:
                    size = other.stat().st_size
                    other.unlink()
                    print(f"deleted: {other.name} ({size / 1e6:.0f} MB)")
    cache = sorted(out.glob("src-*.tsv"))
    print(f"cache: {out} holds {len(cache)} output(s), {sum(item.stat().st_size for item in cache) / 1e6:.0f} MB; base output {base.name}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", required=True, help="the reference revision, for example upstream/dev")
    parser.add_argument("--head", default="HEAD", help="the revision under test (default: HEAD)")
    parser.add_argument("--base-adapter", choices=("auto", "gate", "authorizer"), default="auto")
    parser.add_argument("--head-adapter", choices=("auto", "gate", "authorizer"), default="auto",
                        help="auto reads the production decision; authorizer forces ToolAuthorizer")
    parser.add_argument("--corpus-revision", default=DEFAULT_CORPUS_REVISION)
    parser.add_argument("--states", help="comma-separated state ids, for example bash-none-i-approval,tools-Personal-i-default")
    parser.add_argument("--quick", action="store_true", help="run one Bash, one PowerShell, and one tool state")
    parser.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 2) - 1),
                        help="the number of parallel decision lanes in the probe (default: the CPU count minus 1)")
    parser.add_argument("--out", type=Path, help="the work directory (default: artifacts/authorization-corpus)")
    parser.add_argument("--examples", type=int, default=20, help="the number of differences to print in the report")
    parser.add_argument("--fresh-head", action="store_true",
                        help="probe the head again even when an output exists. With the same revision for --base and --head, "
                             "the report then shows whether two probes of one revision agree.")
    parser.add_argument("--keep", action="store_true",
                        help="keep the head output and the worktrees with their build output (default: delete them after the compare)")
    parser.add_argument("--prune-cache", action="store_true",
                        help="also delete every output in the work directory except the base output")
    arguments = parser.parse_args()
    # A stopped script must still remove its worktree and partial output.
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))

    repository = Path(str(git(Path.cwd(), "rev-parse", "--show-toplevel")).strip())
    out = (arguments.out or repository / "artifacts" / "authorization-corpus").resolve()
    out.mkdir(parents=True, exist_ok=True)
    states = arguments.states
    if arguments.quick and not states:
        states = "bash-anywhere-u-approval,pwsh-none-i-approval,tools-Personal-u-Approval"
        if os.name == "nt":
            states = "pwsh-none-i-approval,tools-Personal-u-Approval"

    if arguments.jobs < 1:
        parser.error("--jobs must be 1 or more")
    timing = Timing(arguments.jobs)
    corpus_started = time.monotonic()
    corpus = build_corpus(repository, resolve(repository, arguments.corpus_revision))
    timing.phases["corpus"] = time.monotonic() - corpus_started
    corpus_path = out / f"corpus-{arguments.corpus_revision}.json"
    # Write and rename, so that a parallel run never reads a partial corpus.
    partial_corpus = corpus_path.with_suffix(f".{os.getpid()}.partial")
    partial_corpus.write_text(json.dumps(corpus, ensure_ascii=False, indent=0), encoding="utf-8")
    partial_corpus.replace(corpus_path)
    print(f"corpus: {len(corpus)} shell inputs from {arguments.corpus_revision}")
    print(f"lanes: {arguments.jobs}, cpus: {os.cpu_count()}")

    base = run_revision(repository, out, arguments.base, arguments.base_adapter, corpus_path, states, arguments.jobs, arguments.keep, timing.phases, "base")
    head = run_revision(repository, out, arguments.head, arguments.head_adapter, corpus_path, states, arguments.jobs, arguments.keep, timing.phases, "head", arguments.fresh_head)
    report = out / f"report-{base.stem}-vs-{head.stem}.txt"
    status = compare(base, head, corpus, report, arguments.examples, out, timing)
    clean_up(out, base, head, arguments.keep, arguments.prune_cache)
    return status


if __name__ == "__main__":
    sys.exit(main())
