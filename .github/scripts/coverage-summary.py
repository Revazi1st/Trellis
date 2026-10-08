"""Summarise a Cobertura coverage report and gate the Core ring on a minimum line rate.

Used by the "Coverage summary and Core gate" step in .github/workflows/ci.yml; standard library
only, so CI needs no extra tool. Run it from the repository root against any Cobertura file:

    python3 .github/scripts/coverage-summary.py coverage.cobertura.xml --min-core-line-rate 90

Every source file is assigned to the ring folder it lives in (src/Core, src/Infrastructure,
src/Presentation, src/Shared, tools), read from the path the PDB recorded and checked against the
checkout, so the report and the gate follow the repository layout rather than assembly names.
Files outside the ring folders (tests, third-party packages that ship symbols, generated code
under obj/) are not counted even when the collection let them in. Lines are counted once per
(file, line) even when a file contributes to several classes (partial types, closures).

The Markdown table goes to $GITHUB_STEP_SUMMARY when that is set, and to stdout always. The exit
code is 1 when the Core ring is below --min-core-line-rate, or has no measured lines at all (the
collection or its filters are broken), and 0 otherwise.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

RINGS = ("src/Core", "src/Infrastructure", "src/Presentation", "src/Shared", "tools")
CONDITION = re.compile(r"\((\d+)/(\d+)\)")


def ring_of(filename: str, repo_root: str) -> str:
    """The ring folder a source file belongs to, or "other" when it is not this repository's.

    A ring folder alone is no proof: third-party packages that ship symbols record paths such as
    /_/src/Shared/... from their own repositories, so the project folder after the ring must
    exist in this checkout too.
    """
    path = "/" + filename.replace("\\", "/").lstrip("/")
    for ring in RINGS:
        marker = f"/{ring}/"
        start = path.find(marker)
        if start < 0:
            continue
        project = path[start + len(marker):].split("/", 1)[0]
        if project and os.path.isdir(os.path.join(repo_root, ring, project)):
            return ring
    return "other"


class Tally:
    def __init__(self) -> None:
        self.lines: dict[tuple[str, int], int] = {}
        self.branches: dict[tuple[str, int], tuple[int, int]] = {}

    def add(self, filename: str, line: ET.Element) -> None:
        key = (filename, int(line.get("number", "0")))
        self.lines[key] = max(self.lines.get(key, 0), int(line.get("hits", "0")))
        match = CONDITION.search(line.get("condition-coverage", ""))
        if match:
            covered, total = int(match.group(1)), int(match.group(2))
            previous = self.branches.get(key, (0, 0))
            self.branches[key] = (max(previous[0], covered), max(previous[1], total))

    def merge(self, other: "Tally") -> None:
        for key, hits in other.lines.items():
            self.lines[key] = max(self.lines.get(key, 0), hits)
        for key, (covered, total) in other.branches.items():
            previous = self.branches.get(key, (0, 0))
            self.branches[key] = (max(previous[0], covered), max(previous[1], total))

    @property
    def lines_valid(self) -> int:
        return len(self.lines)

    @property
    def lines_covered(self) -> int:
        return sum(1 for hits in self.lines.values() if hits > 0)

    @property
    def line_rate(self) -> float:
        return 100.0 * self.lines_covered / self.lines_valid if self.lines_valid else 0.0

    @property
    def branch_text(self) -> str:
        total = sum(t for _, t in self.branches.values())
        covered = sum(c for c, _ in self.branches.values())
        return f"{100.0 * covered / total:.1f}%" if total else "n/a"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("report", help="Cobertura XML file (dotnet-coverage -f cobertura)")
    parser.add_argument("--min-core-line-rate", type=float, default=0.0,
                        help="fail when the src/Core line rate (percent) is below this")
    parser.add_argument("--repo-root", default=".",
                        help="the checkout the ring folders are resolved against")
    args = parser.parse_args()

    root = ET.parse(args.report).getroot()
    by_assembly: dict[tuple[str, str], Tally] = defaultdict(Tally)
    by_ring: dict[str, Tally] = defaultdict(Tally)
    for package in root.iter("package"):
        assembly = package.get("name", "?")
        for cls in package.iter("class"):
            filename = cls.get("filename", "")
            ring = ring_of(filename, args.repo_root)
            lines = cls.find("lines")
            for line in (lines.iter("line") if lines is not None else ()):
                by_assembly[(ring, assembly)].add(filename, line)
    rows = {key: tally for key, tally in by_assembly.items() if key[0] != "other"}
    for (ring, _), tally in rows.items():
        by_ring[ring].merge(tally)

    total = Tally()
    for tally in by_ring.values():
        total.merge(tally)
    core = by_ring.get("src/Core", Tally())

    out = [
        "## Code coverage",
        "",
        f"Core ring (`src/Core`): **{core.line_rate:.1f}%** of {core.lines_valid} lines "
        f"(gate: at least {args.min_core_line_rate:g}%). "
        f"All rings: {total.line_rate:.1f}% of {total.lines_valid} lines.",
        "",
        "| Ring | Assembly | Lines | Line rate | Branch rate |",
        "|---|---|---:|---:|---:|",
    ]
    for (ring, assembly), tally in sorted(rows.items()):
        out.append(f"| {ring} | {assembly} | {tally.lines_covered}/{tally.lines_valid} "
                   f"| {tally.line_rate:.1f}% | {tally.branch_text} |")
    out.append("")
    summary = "\n".join(out) + "\n"

    target = os.environ.get("GITHUB_STEP_SUMMARY")
    if target:
        with open(target, "a", encoding="utf-8") as handle:
            handle.write(summary)
    print(summary)

    if core.lines_valid == 0:
        print("::error::No src/Core lines in the coverage report: the collection or its filters "
              "(eng/coverage.runsettings) are broken, or this is not run from the repository root.")
        return 1
    if core.line_rate < args.min_core_line_rate:
        print(f"::error::Core ring line coverage {core.line_rate:.1f}% is below the "
              f"{args.min_core_line_rate:g}% gate. Add tests for the uncovered Core code.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
