#!/usr/bin/env python3
"""Create one local effect with an exclusive file receipt."""

import os
from pathlib import Path
import sys


def main():
    if len(sys.argv) != 3:
        raise ValueError("Supply the assigned effect path and nonce.")
    path, nonce = sys.argv[1:]
    if not Path(path).is_absolute() or not nonce.isalnum():
        raise ValueError("The assigned effect path or nonce is invalid.")
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600), "wb") as stream:
        stream.write(nonce.encode())
    print(nonce)


if __name__ == "__main__":
    main()
