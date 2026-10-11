"""Summarise TRX test results: totals per test run, then every failure, in the job summary.

Used by the "Test summary" step in .github/workflows/ci.yml; standard library only, so CI needs no
extra tool, and no third-party reporting action (which would need the checks: write permission).
Run it from the repository root after a test run that wrote TRX files:

    dotnet test <solution> -- --report-trx --results-directory TestResults
    python3 .github/scripts/trx-summary.py TestResults

Each argument is a TRX file or a directory searched recursively for *.trx (default: TestResults).
Every test result counts once: Passed as passed; Failed, Error, Timeout and Aborted as failed;
NotExecuted, NotRunnable and Inconclusive as skipped (an xunit Skip, an opt-in suite left off).
The summary shows one row per TRX file, every failure with its message and stack trace, and the
skipped tests with their reasons; a run that failed without a failed test (a crashed test host,
a run that found no tests) shows the reason the test platform recorded.

The Markdown goes to $GITHUB_STEP_SUMMARY when that is set, and to stdout always; on GitHub Actions
the first failures also become error annotations on the run. The exit code is 0 whatever the
results: the test step itself fails the job. Finding no TRX file at all prints a warning.
"""

from __future__ import annotations

import argparse
import html
import os
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from datetime import datetime

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
FAILED = {"Failed", "Error", "Timeout", "Aborted"}
SKIPPED = {"NotExecuted", "NotRunnable", "Inconclusive"}
MAX_ANNOTATIONS = 10   # GitHub shows at most ten error annotations per step
MAX_SKIPPED_ROWS = 100
MAX_DETAIL_CHARS = 6000


@dataclass
class Failure:
    name: str
    message: str
    stack: str


@dataclass
class Run:
    label: str
    passed: int = 0
    failed: int = 0
    skipped: int = 0
    seconds: float | None = None
    failures: list[Failure] = field(default_factory=list)
    skips: list[tuple[str, str]] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    @property
    def total(self) -> int:
        return self.passed + self.failed + self.skipped


def find_trx(paths: list[str]) -> list[str]:
    found: set[str] = set()
    for path in paths:
        if os.path.isfile(path):
            found.add(path)
        elif os.path.isdir(path):
            for directory, _, files in os.walk(path):
                found.update(os.path.join(directory, name) for name in files
                             if name.lower().endswith(".trx"))
    return sorted(found)


def text_of(element: ET.Element | None, path: str) -> str:
    if element is None:
        return ""
    found = element.find(path, NS)
    # Leading spaces stay: a stack trace's first frame is indented like the others.
    return (found.text or "").lstrip("\r\n").rstrip() if found is not None else ""


def timestamp(value: str) -> datetime:
    # Python before 3.11 parses neither a trailing Z nor more than six fractional digits.
    value = re.sub(r"(\.\d{6})\d+", r"\1", value.strip()).replace("Z", "+00:00")
    return datetime.fromisoformat(value)


def wall_clock(root: ET.Element) -> float | None:
    times = root.find("t:Times", NS)
    if times is None:
        return None
    try:
        start = timestamp(times.get("start", ""))
        finish = timestamp(times.get("finish", ""))
    except ValueError:
        return None
    return max((finish - start).total_seconds(), 0.0)


def read_run(path: str) -> Run:
    # The file name is "<test project>_<framework>_<architecture>.trx" by default, so it says
    # which project ran; the full name is kept when it does not follow that shape.
    label = re.sub(r"_net\d[^_]*_[^_]+$", "", os.path.splitext(os.path.basename(path))[0])
    run = Run(label=label)
    root = ET.parse(path).getroot()
    run.seconds = wall_clock(root)
    for result in root.iterfind("t:Results/t:UnitTestResult", NS):
        name = result.get("testName", "?")
        outcome = result.get("outcome", "")
        output = result.find("t:Output", NS)
        if outcome == "Passed":
            run.passed += 1
        elif outcome in FAILED:
            run.failed += 1
            run.failures.append(Failure(name, text_of(output, "t:ErrorInfo/t:Message"),
                                        text_of(output, "t:ErrorInfo/t:StackTrace")))
        elif outcome in SKIPPED:
            run.skipped += 1
            reason = text_of(output, "t:ErrorInfo/t:Message") or text_of(output, "t:StdOut")
            run.skips.append((name, reason))
        else:
            run.notes.append(f"{name}: outcome '{outcome}'")
    summary = root.find("t:ResultSummary", NS)
    if summary is not None and summary.get("outcome") not in (None, "Completed", "Passed") \
            and not run.failures:
        for info in summary.iterfind("t:RunInfos/t:RunInfo", NS):
            if info.get("outcome") in ("Error", "Failed", "Aborted"):
                run.notes.append(text_of(info, "t:Text"))
        if not run.notes:
            run.notes.append(f"the run's outcome is '{summary.get('outcome')}'")
    return run


def duration(seconds: float | None) -> str:
    if seconds is None:
        return "n/a"
    if seconds < 60:
        return f"{seconds:.1f} s"
    minutes, rest = divmod(int(round(seconds)), 60)
    return f"{minutes} min {rest:02d} s"


def cell(text: str) -> str:
    """Text safe inside a Markdown table cell."""
    return html.escape(" ".join(text.split()), quote=False).replace("|", "&#124;")


def fenced(text: str) -> str:
    """A code block whose fence is longer than any backtick run in the text."""
    if len(text) > MAX_DETAIL_CHARS:
        text = text[:MAX_DETAIL_CHARS] + "\n[... truncated; the TRX file has the rest]"
    longest = max((len(run) for run in re.findall(r"`+", text)), default=0)
    fence = "`" * max(3, longest + 1)
    return f"{fence}text\n{text}\n{fence}"


def annotation(text: str, *, property_value: bool = False) -> str:
    """Escapes a workflow-command value (and, for a property such as title, its separators)."""
    text = text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    if property_value:
        text = text.replace(":", "%3A").replace(",", "%2C")
    return text


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("paths", nargs="*", default=["TestResults"],
                        help="TRX files, or directories searched for *.trx (default: TestResults)")
    args = parser.parse_args()

    files = find_trx(args.paths)
    if not files:
        message = (f"No TRX files under {', '.join(args.paths)}: the tests did not run, or ran "
                   "without '-- --report-trx --results-directory TestResults'.")
        print(f"::warning::{message}")
        target = os.environ.get("GITHUB_STEP_SUMMARY")
        if target:
            with open(target, "a", encoding="utf-8") as handle:
                handle.write(f"## Tests\n\n{message}\n")
        return 0

    runs: list[Run] = []
    for path in files:
        try:
            runs.append(read_run(path))
        except ET.ParseError as error:
            print(f"::warning::{path} is not a readable TRX file: {error}")
    if not runs:
        return 0

    passed = sum(run.passed for run in runs)
    failed = sum(run.failed for run in runs)
    skipped = sum(run.skipped for run in runs)
    total = sum(run.total for run in runs)
    verdict = "failed" if failed or any(run.notes for run in runs) else "passed"

    out = [
        "## Tests",
        "",
        f"**{passed} passed, {failed} failed, {skipped} skipped** of {total} tests in "
        f"{len(runs)} test run(s): {verdict}.",
        "",
        "| Test run | Passed | Failed | Skipped | Duration |",
        "|---|---:|---:|---:|---:|",
    ]
    for run in sorted(runs, key=lambda r: r.label):
        out.append(f"| {cell(run.label)} | {run.passed} | {run.failed} | {run.skipped} "
                   f"| {duration(run.seconds)} |")
    out.append("")

    failures = [(run, failure) for run in runs for failure in run.failures]
    if failures:
        out += [f"### Failures ({len(failures)})", ""]
        for run, failure in failures:
            details = "\n".join(part for part in (failure.message, failure.stack) if part)
            out += [
                f"<details><summary><code>{html.escape(failure.name, quote=False)}</code> "
                f"({html.escape(run.label, quote=False)})</summary>",
                "",
                fenced(details or "(no message recorded)"),
                "",
                "</details>",
                "",
            ]

    noted = [(run, note) for run in runs for note in run.notes]
    if noted:
        out += ["### Runs that failed without a failed test", ""]
        out += [f"- {cell(run.label)}: {cell(note)}" for run, note in noted]
        out.append("")

    skips = [(run, name, reason) for run in runs for name, reason in run.skips]
    if skips:
        out += [f"<details><summary>Skipped tests ({len(skips)})</summary>", "",
                "| Test | Reason |", "|---|---|"]
        for run, name, reason in skips[:MAX_SKIPPED_ROWS]:
            out.append(f"| {cell(name)} | {cell(reason) or 'no reason recorded'} |")
        if len(skips) > MAX_SKIPPED_ROWS:
            out.append(f"| ... and {len(skips) - MAX_SKIPPED_ROWS} more | |")
        out += ["", "</details>", ""]

    summary = "\n".join(out) + "\n"
    target = os.environ.get("GITHUB_STEP_SUMMARY")
    if target:
        with open(target, "a", encoding="utf-8") as handle:
            handle.write(summary)
    print(summary)

    if os.environ.get("GITHUB_ACTIONS") == "true":
        for run, failure in failures[:MAX_ANNOTATIONS]:
            first_line = failure.message.splitlines()[0] if failure.message else "failed"
            print(f"::error title={annotation('Test failed: ' + failure.name, property_value=True)}"
                  f"::{annotation(run.label + ': ' + first_line)}")
        if len(failures) > MAX_ANNOTATIONS:
            print(f"::error::{len(failures) - MAX_ANNOTATIONS} more test failure(s): "
                  "see the job summary.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
