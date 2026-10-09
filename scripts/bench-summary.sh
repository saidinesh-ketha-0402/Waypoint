#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <results.csv> [repo]" >&2
  exit 1
fi

CSV_PATH="$1"
REPO_PATH="${2:-.}"

if [[ ! -f "$CSV_PATH" ]]; then
  echo "error: CSV not found: $CSV_PATH" >&2
  exit 1
fi

python3 - "$CSV_PATH" "$REPO_PATH" <<'PY'
import csv
import json
import re
import statistics
import sys
from collections import defaultdict
from pathlib import Path

csv_path = Path(sys.argv[1])
repo_path = Path(sys.argv[2])

rows = []
with csv_path.open("r", encoding="utf-8") as f:
    reader = csv.DictReader(f)
    rows = list(reader)

if not rows:
    print("No benchmark rows found.")
    sys.exit(0)

metrics = [
    ("input_tokens", True),
    ("tool_calls", True),
    ("dotnet_build_test_runs", True),
    ("wall_secs", False),
]

def as_float(row, key):
    try:
        return float(row.get(key, "") or 0.0)
    except ValueError:
        return 0.0

def fmt_num(v, integer=True):
    if integer:
        return f"{int(round(v))}"
    return f"{v:.2f}"

def med_and_range(values):
    med = statistics.median(values)
    return med, min(values), max(values)

def arm_rows(arm):
    return [r for r in rows if r.get("arm") == arm]

a_rows = arm_rows("A")
b_rows = arm_rows("B")

print("| metric | A cold (median [range]) | B Waypoint | Δ |")
print("|---|---|---|---|")

for key, integer in metrics:
    a_vals = [as_float(r, key) for r in a_rows]
    b_vals = [as_float(r, key) for r in b_rows]
    if not a_vals or not b_vals:
        continue
    a_med, a_min, a_max = med_and_range(a_vals)
    b_med, b_min, b_max = med_and_range(b_vals)
    if a_med == 0:
        delta = "n/a"
    else:
        pct = ((b_med - a_med) / a_med) * 100.0
        delta = f"{pct:+.0f} %"
    print(
        f"| {key} | {fmt_num(a_med, integer)} [{fmt_num(a_min, integer)}-{fmt_num(a_max, integer)}] "
        f"| {fmt_num(b_med, integer)} [{fmt_num(b_min, integer)}-{fmt_num(b_max, integer)}] | {delta} |"
    )

if a_rows and b_rows:
    a_pass = sum(1 for r in a_rows if (r.get("tests_pass") or "0") == "1")
    b_pass = sum(1 for r in b_rows if (r.get("tests_pass") or "0") == "1")
    a_dead = sum(1 for r in a_rows if (r.get("repeated_dead_end") or "0") == "1")
    b_dead = sum(1 for r in b_rows if (r.get("repeated_dead_end") or "0") == "1")
    print(f"| tests_pass | {a_pass}/{len(a_rows)} | {b_pass}/{len(b_rows)} | = |")
    print(f"| repeated_dead_end | {a_dead}/{len(a_rows)} | {b_dead}/{len(b_rows)} | |")

model = rows[-1].get("model", "<unknown>")
print(f"n={len(a_rows)} per arm · single task · single solution · model {model}")

hooks_log = repo_path / ".git" / "waypoint" / "hooks.log"
if not hooks_log.exists():
    print("\nHook latency medians: hooks log not found.")
    sys.exit(0)

event_latencies = defaultdict(list)
json_fail = 0
with hooks_log.open("r", encoding="utf-8") as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        try:
            payload = json.loads(line)
            event = payload.get("event") or payload.get("hook") or "unknown"
            ms = payload.get("latency_ms") or payload.get("duration_ms")
            if isinstance(ms, (int, float)):
                event_latencies[event].append(float(ms))
                continue
        except json.JSONDecodeError:
            json_fail += 1

        evt_match = re.search(r"(event|hook)=([A-Za-z0-9._-]+)", line)
        ms_match = re.search(r"([0-9]+(?:\.[0-9]+)?)\s*ms", line)
        if evt_match and ms_match:
            event_latencies[evt_match.group(2)].append(float(ms_match.group(1)))

print("\nHook latency medians (arm B runs):")
if not event_latencies:
    if json_fail:
        print("- unable to parse hook latencies from hooks.log")
    else:
        print("- no hook latency entries found")
else:
    for event in sorted(event_latencies):
        med = statistics.median(event_latencies[event])
        print(f"- {event}: {med:.1f} ms")
PY
