#!/usr/bin/env python3
"""Shared SemVer 2.0.0 precedence key.

Used by generate-release-manifest.sh to compute `latest` / `latestPrerelease`, and by the
conformance check (`--check <fixture>`) that guards against this key drifting from the C#
comparator in src/Netclaw.Configuration/Feeds/SemVer.cs. Both sides are asserted against
the same ordered fixture (feeds/scripts/semver-order.txt), so a divergence fails CI.

Keep semver_key() in lockstep with SemVer.Compare in the C# comparator.
"""
import sys


def semver_key(version):
    """Return a tuple that sorts version strings by SemVer 2.0.0 precedence.

    Build metadata is ignored. A version without a prerelease outranks one with the same
    core; prerelease identifiers compare per spec (numeric < alphanumeric, numeric
    compared as integers, longer identifier set wins when the shared prefix is equal).
    """
    core, _, pre = version.partition('-')
    pre = pre.split('+')[0]  # drop build metadata
    parts = (core.split('.') + ['0', '0', '0'])[:3]
    try:
        nums = tuple(int(x) for x in parts)
    except ValueError:
        nums = (0, 0, 0)
    if not pre:
        return (nums, 1, ())
    ids = [(0, int(p), '') if p.isdigit() else (1, 0, p) for p in pre.split('.')]
    return (nums, 0, tuple(ids))


def _read_versions(path):
    with open(path) as f:
        return [ln.strip() for ln in f if ln.strip() and not ln.lstrip().startswith('#')]


def _main(argv):
    # --check <fixture>: assert the fixture's lines are already in ascending precedence
    # order (i.e. sorting by semver_key reproduces the file). This is the bash side of the
    # cross-language conformance test; the C# SemVerConformanceTests asserts the same file.
    if len(argv) >= 3 and argv[1] == '--check':
        versions = _read_versions(argv[2])
        ordered = sorted(versions, key=semver_key)
        if ordered != versions:
            print('SemVer conformance FAILED: generator key disagrees with fixture order',
                  file=sys.stderr)
            print(f'  fixture order: {versions}', file=sys.stderr)
            print(f'  sorted  order: {ordered}', file=sys.stderr)
            return 1
        print(f'SemVer conformance OK: {len(versions)} versions ordered identically')
        return 0

    # Default: sort the version strings on stdin and print them in ascending order.
    versions = [ln.strip() for ln in sys.stdin if ln.strip()]
    for v in sorted(versions, key=semver_key):
        print(v)
    return 0


if __name__ == '__main__':
    sys.exit(_main(sys.argv))
