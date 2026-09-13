#!/usr/bin/env python3
"""Count pixels that differ between two PNG files."""

from pathlib import Path
import struct
import subprocess
import sys


def decode_rgba(path: Path) -> tuple[tuple[int, int], bytes]:
    header = path.read_bytes()[:24]
    if len(header) != 24 or header[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} is not a PNG file.")

    dimensions = struct.unpack(">II", header[16:24])
    result = subprocess.run(
        [
            "ffmpeg",
            "-v",
            "error",
            "-i",
            str(path),
            "-f",
            "rawvideo",
            "-pix_fmt",
            "rgba",
            "-frames:v",
            "1",
            "-",
        ],
        check=True,
        stdout=subprocess.PIPE,
    )
    return dimensions, result.stdout


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: count-png-differences.py <baseline.png> <actual.png>", file=sys.stderr)
        return 2

    try:
        baseline_dimensions, baseline = decode_rgba(Path(sys.argv[1]))
        actual_dimensions, actual = decode_rgba(Path(sys.argv[2]))
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1

    if baseline_dimensions != actual_dimensions or len(baseline) != len(actual):
        print("PNG dimensions do not match.", file=sys.stderr)
        return 1

    differences = sum(
        baseline[index : index + 4] != actual[index : index + 4]
        for index in range(0, len(baseline), 4)
    )
    print(differences)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
