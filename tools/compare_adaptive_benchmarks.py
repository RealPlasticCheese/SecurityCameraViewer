"""Repeat the test-first adaptive benchmark against two saved Release builds."""

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess


def summarize(runs):
    grouped = {label: [] for label in ("before", "after")}
    names = None
    for run in runs:
        label = run["build"]
        if label not in grouped:
            raise ValueError("Unknown build label")
        cases = {case["name"]: case for case in run["data"]["results"]}
        if not cases or len(cases) != len(run["data"]["results"]):
            raise ValueError("Empty or duplicate benchmark cases")
        if names is None:
            names = set(cases)
        if set(cases) != names:
            raise ValueError("Benchmark cases differ between runs")
        for case in cases.values():
            elapsed, allocated = case["millisecondsPerOperation"], case["bytesPerOperation"]
            if not math.isfinite(elapsed) or elapsed <= 0 or not math.isfinite(allocated) or allocated < 0:
                raise ValueError("Invalid benchmark measurement")
        grouped[label].append(cases)
    if not grouped["before"] or len(grouped["before"]) != len(grouped["after"]):
        raise ValueError("Both builds require the same nonzero number of runs")
    results = []
    for name in sorted(names):
        result = {"name": name}
        for label, cases in grouped.items():
            times = [run[name]["millisecondsPerOperation"] for run in cases]
            allocations = [run[name]["bytesPerOperation"] for run in cases]
            result[label] = {
                "medianMilliseconds": statistics.median(times),
                "minimumMilliseconds": min(times),
                "maximumMilliseconds": max(times),
                "medianBytes": statistics.median(allocations),
            }
        result["reductionPercent"] = 100 * (1 - result["after"]["medianMilliseconds"] / result["before"]["medianMilliseconds"])
        results.append(result)
    return results


def compare(before, after, runs, iterations):
    if runs <= 0 or iterations <= 0:
        raise ValueError("Runs and iterations must be positive")
    binaries = {label: Path(directory).resolve() / "SecurityCameraViewer.Recording.Tests.dll"
                for label, directory in (("before", before), ("after", after))}
    for binary in binaries.values():
        if not binary.is_file():
            raise FileNotFoundError(binary)
    environment = os.environ.copy()
    # Use optimized JIT code from the first call, eliminating process-start tier transitions.
    environment["DOTNET_TieredCompilation"] = "0"
    samples = []
    for pair in range(runs):
        for label in (("before", "after") if pair % 2 == 0 else ("after", "before")):
            print(f"Pair {pair + 1}/{runs}: {label}", flush=True)
            completed = subprocess.run(["dotnet", str(binaries[label]), "--adaptive-benchmark", str(iterations)],
                                       env=environment, capture_output=True, text=True, encoding="utf-8", check=True)
            samples.append({"pair": pair + 1, "build": label, "data": json.loads(completed.stdout)})
    return {
        "runsPerBuild": runs,
        "iterations": iterations,
        "tieredCompilation": False,
        "testAssemblySha256": {label: hashlib.sha256(binary.read_bytes()).hexdigest() for label, binary in binaries.items()},
        "runs": samples,
        "summary": summarize(samples),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--iterations", type=int, default=500)
    args = parser.parse_args()
    result = compare(args.before, args.after, args.runs, args.iterations)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(args.output)


if __name__ == "__main__":
    main()
