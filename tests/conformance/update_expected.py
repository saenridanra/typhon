# Copyright (c) 2026 The Typhon contributors.
# Licensed under the MIT License. See LICENSE file in the project root for full license information.

"""Regenerate tests/conformance/expected/*.out by running every case on CPython.

CPython is the oracle: a Typhon program is conformant when it prints exactly what
CPython prints for the same .ty file. Run this after adding or changing a case:

    python tests/conformance/update_expected.py            # all cases
    python tests/conformance/update_expected.py strings    # only cases whose name contains "strings"

The numpy_* cases need numpy installed in the interpreter that runs this script.
"""

import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
CASES = HERE / "cases"
EXPECTED = HERE / "expected"


def main() -> int:
    filters = sys.argv[1:]
    EXPECTED.mkdir(exist_ok=True)
    env = dict(os.environ, PYTHONIOENCODING="utf-8", PYTHONHASHSEED="0")
    failed = 0
    for case in sorted(CASES.glob("*.ty")):
        if filters and not any(f in case.stem for f in filters):
            continue
        proc = subprocess.run(
            [sys.executable, "-X", "utf8", str(case)],
            capture_output=True,
            env=env,
            cwd=CASES,
            timeout=120,
        )
        if proc.returncode != 0:
            failed += 1
            print(f"FAIL {case.stem}: exit {proc.returncode}\n{proc.stderr.decode('utf-8', 'replace')}")
            continue
        out = proc.stdout.decode("utf-8").replace("\r\n", "\n")
        (EXPECTED / f"{case.stem}.out").write_text(out, encoding="utf-8", newline="\n")
        print(f"ok   {case.stem} ({len(out.splitlines())} lines)")
    print(f"Python {sys.version.split()[0]}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
