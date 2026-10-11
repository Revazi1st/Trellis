"""Summarise a Cobertura coverage report and gate each ring on a minimum line rate.

Used by the "Coverage summary and ring floors" step in .github/workflows/ci.yml; standard library
only, so CI needs no extra tool. Run it from the repository root against any Cobertura file:

    python3 .github/scripts/coverage-summary.py coverage.cobertura.xml \\
        --min-line-rate src/Core=90 --min-line-rate src/Infrastructure=85

Every source file is assigned to the ring folder it lives in (src/Core, src/Infrastructure,
src/Presentation, src/Shared, tools), read from the path the PDB recorded and checked against the
checkout, so the report and the gate follow the repository layout rather than assembly names.
Files outside the ring folders (tests, third-party packages that ship symbols, generated code
under obj/) are not counted even when the collection let them in. Neither is build-time tooling:
the in-repository analyzer (a project under tools/ whose name ends in .Analyzers, ADR-0030) runs
inside the compiler and is never deployed, so eng/coverage.runsettings leaves its assembly out of
the collection and ring_of skips its files as well; the tools row (AppHost, migration job) is the
same with or without the settings file, and no ring floor ever depends on the analyzer. Lines are
counted once per (file, line) even when a file contributes to several classes (partial types,
closures).

--min-line-rate RING=PERCENT sets one ring's floor and may be repeated; a ring without one is
reported but not gated. --min-core-line-rate PERCENT is the older spelling of
--min-line-rate src/Core=PERCENT and still works.

The Markdown tables go to $GITHUB_STEP_SUMMARY when that is set, and to stdout always. The exit
code is 1 when a ring is below its floor, when a ring with a floor has no measured lines, or when
the Core ring has no measured lines at all (either way the collection or its filters are broken),
and 0 otherwise.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

RINGS = ("src/Core", "src/Infrastructure", "src/Presentation", "src/Shared", "tools")
# A project under tools/ whose name ends with this is build-time tooling, not measured (docstring).
BUILD_TIME_SUFFIX = ".Analyzers"
NOT_COUNTED = ("other", "build-time")
CONDITION = re.compile(r"\((\d+)/(\d+)\)")


def ring_of(filename: str, repo_root: str) -> str:
    """The ring folder a source file belongs to, "build-time" for the analyzer project, or "other"
    when it is not this repository's.

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
            if ring == "tools" and project.endswith(BUILD_TIME_SUFFIX):
                return "build-time"
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


def floor_arg(text: str) -> tuple[str, float]:
    """One --min-line-rate value: RING=PERCENT, with RING one of RINGS."""
    ring, separator, percent = text.partition("=")
    ring = ring.strip().rstrip("/")
    if not separator or ring not in RINGS:
        raise argparse.ArgumentTypeError(
            f"expected RING=PERCENT with RING one of {', '.join(RINGS)}, got '{text}'")
    try:
        value = float(percent)
    except ValueError:
        raise argparse.ArgumentTypeError(f"'{percent}' is not a number") from None
    if not 0.0 <= value <= 100.0:
        raise argparse.ArgumentTypeError(f"{value:g} is not a percentage between 0 and 100")
    return ring, value


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("report", help="Cobertura XML file (dotnet-coverage -f cobertura)")
    parser.add_argument("--min-line-rate", type=floor_arg, action="append", default=[],
                        metavar="RING=PERCENT",
                        help="fail when RING's line rate (percent) is below PERCENT; repeat it "
                             "for each ring that has a floor")
    parser.add_argument("--min-core-line-rate", type=float, metavar="PERCENT",
                        help="the older spelling of --min-line-rate src/Core=PERCENT (which wins "
                             "when both are given)")
    parser.add_argument("--repo-root", default=".",
                        help="the checkout the ring folders are resolved against")
    args = parser.parse_args()

    floors: dict[str, float] = {}
    if args.min_core_line_rate is not None:
        floors["src/Core"] = args.min_core_line_rate
    floors.update(dict(args.min_line_rate))

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
    rows = {key: tally for key, tally in by_assembly.items() if key[0] not in NOT_COUNTED}
    for (ring, _), tally in rows.items():
        by_ring[ring].merge(tally)

    total = Tally()
    for tally in by_ring.values():
        total.merge(tally)

    errors: list[str] = []
    if by_ring["src/Core"].lines_valid == 0:
        errors.append("No src/Core lines in the coverage report: the collection or its filters "
                      "(eng/coverage.runsettings) are broken, or this is not run from the "
                      "repository root.")
    ring_rows = []
    for ring in RINGS:
        tally = by_ring[ring]
        floor = floors.get(ring)
        if tally.lines_valid == 0 and floor is None:
            continue
        floor_text = "none" if floor is None else f"{floor:g}%"
        if floor is not None and tally.lines_valid == 0 and ring != "src/Core":
            errors.append(f"No {ring} lines in the coverage report, although the ring has a "
                          f"{floor:g}% floor: the collection or its filters are broken, or the "
                          f"ring no longer exists and its floor should go.")
            floor_text = f"**{floor:g}%, no lines**"
        elif floor is not None and tally.lines_valid and tally.line_rate < floor:
            errors.append(f"{ring} line coverage {tally.line_rate:.1f}% is below its {floor:g}% "
                          f"floor. Add tests for the uncovered code in {ring}.")
            floor_text = f"**{floor:g}%, below**"
        ring_rows.append(f"| {ring} | {tally.lines_covered}/{tally.lines_valid} "
                         f"| {tally.line_rate:.1f}% | {tally.branch_text} | {floor_text} |")

    out = [
        "## Code coverage",
        "",
        f"All rings: {total.line_rate:.1f}% of {total.lines_valid} lines. A ring below its floor "
        f"fails the job; a ring without one is reported only.",
        "",
        "| Ring | Lines | Line rate | Branch rate | Floor |",
        "|---|---:|---:|---:|---:|",
        *ring_rows,
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

    for error in errors:
        print(f"::error::{error}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
