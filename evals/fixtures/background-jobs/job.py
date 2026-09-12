#!/usr/bin/env python3
"""Harmless process fixture. The harness owns release through a socket barrier."""

import json
import os
from pathlib import Path
import sys
import urllib.request


def main():
    port, nonce, slot = sys.argv[1:]
    marker = Path(__file__).parent / "markers" / f"{nonce}-{slot}"
    marker.parent.mkdir(exist_ok=True)
    # Write before the network call. A failed callback must not conceal a process start.
    with marker.open("x") as output:
        output.write(str(os.getpid()))
    print(f"background-eval-ready {nonce} {slot}", flush=True)
    hold = Path(sys.argv[0]).name == "hold_job"
    payload = json.dumps({"nonce": nonce, "slot": slot, "pid": os.getpid()}).encode()
    request = urllib.request.Request(
        f"http://127.0.0.1:{int(port)}/fixture/{'hold' if hold else 'mark'}",
        data=payload, headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(request, timeout=600) as response:
        if response.status != 200:
            raise RuntimeError("The fixture callback failed.")


if __name__ == "__main__":
    main()
