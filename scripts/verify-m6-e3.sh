#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURES_DIR="$ROOT_DIR/tests/fixtures/hooks"

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <repo-root>"
  echo
  echo "Example:"
  echo "  $0 \"$HOME/demo/DemoShop\""
  exit 1
fi

REPO_ROOT="$(cd "$1" && pwd)"
if [[ ! -d "$REPO_ROOT/.git" ]]; then
  echo "error: expected a git repo with a .git directory at: $REPO_ROOT"
  exit 1
fi

if [[ -n "${WAYPOINT_BIN:-}" ]]; then
  if ! command -v "$WAYPOINT_BIN" >/dev/null 2>&1; then
    echo "error: '$WAYPOINT_BIN' is not on PATH."
    exit 1
  fi
  WAYPOINT_RUNNER=("$WAYPOINT_BIN")
  WAYPOINT_RUNNER_DESC="$WAYPOINT_BIN"
else
  WAYPOINT_RUNNER=(
    dotnet run
    --project "$ROOT_DIR/src/Waypoint.Cli/Waypoint.Cli.csproj"
    -p:NuGetAudit=false
    --
  )
  WAYPOINT_RUNNER_DESC="dotnet run --project src/Waypoint.Cli/Waypoint.Cli.csproj -p:NuGetAudit=false --"
fi

required_fixtures=(
  beforeSubmitPrompt.json
  afterFileEdit.cs.json
  preCompact.json
  stop.json
)
for fixture in "${required_fixtures[@]}"; do
  if [[ ! -f "$FIXTURES_DIR/$fixture" ]]; then
    echo "error: missing fixture file: $FIXTURES_DIR/$fixture"
    exit 1
  fi
done

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/waypoint-m6-e3-XXXXXX")"
cleanup() {
  rm -rf "$tmp_dir"
}
trap cleanup EXIT

run_id="$(date +%s)-$$"
resume_path="$REPO_ROOT/.cursor/waypoint/RESUME.md"

render_fixture() {
  local fixture_name="$1"
  local destination="$2"
  sed "s#{{REPO}}#$REPO_ROOT#g" "$FIXTURES_DIR/$fixture_name" > "$destination"
}

assert_is_empty_object() {
  local json_path="$1"
  local label="$2"
  python3 - "$json_path" "$label" <<'PY'
import json
import sys

path, label = sys.argv[1], sys.argv[2]
with open(path, "r", encoding="utf-8") as f:
    payload = json.load(f)

if payload != {}:
    print(f"assertion failed ({label}): expected '{{}}' but got {payload!r}", file=sys.stderr)
    sys.exit(1)
PY
}

assert_has_user_message() {
  local json_path="$1"
  local label="$2"
  python3 - "$json_path" "$label" <<'PY'
import json
import sys

path, label = sys.argv[1], sys.argv[2]
with open(path, "r", encoding="utf-8") as f:
    payload = json.load(f)

msg = payload.get("user_message")
if not isinstance(msg, str) or not msg.startswith("Waypoint checkpoint:"):
    print(f"assertion failed ({label}): expected checkpoint user_message", file=sys.stderr)
    sys.exit(1)
PY
}

echo "Using repo: $REPO_ROOT"
echo "Using waypoint runner: $WAYPOINT_RUNNER_DESC"
echo "Preparing hook payload fixtures in: $tmp_dir"

render_fixture beforeSubmitPrompt.json "$tmp_dir/beforeSubmitPrompt.json"
render_fixture afterFileEdit.cs.json "$tmp_dir/afterFileEdit.cs.json"
render_fixture preCompact.json "$tmp_dir/preCompact.json"
render_fixture stop.json "$tmp_dir/stop.json"

for f in "$tmp_dir"/*.json; do
  sed -i '' \
    -e "s/conv_demo_001/conv_m6_${run_id}/g" \
    -e "s/sess_demo_001/sess_m6_${run_id}/g" \
    "$f"
done

echo "Step 1/6: ensure active task/session"
"${WAYPOINT_RUNNER[@]}" hook beforeSubmitPrompt < "$tmp_dir/beforeSubmitPrompt.json" > "$tmp_dir/out-beforeSubmitPrompt.json"

echo "Step 2/6: register a .cs edit (to trigger stop background indexing path)"
"${WAYPOINT_RUNNER[@]}" hook afterFileEdit < "$tmp_dir/afterFileEdit.cs.json" > "$tmp_dir/out-afterFileEdit.json"
assert_is_empty_object "$tmp_dir/out-afterFileEdit.json" "afterFileEdit response"

echo "Step 3/6: run preCompact checkpoint"
"${WAYPOINT_RUNNER[@]}" hook preCompact < "$tmp_dir/preCompact.json" > "$tmp_dir/out-preCompact.json"
assert_has_user_message "$tmp_dir/out-preCompact.json" "preCompact response"

echo "Step 4/6: run stop checkpoint + resume generation"
"${WAYPOINT_RUNNER[@]}" hook stop < "$tmp_dir/stop.json" > "$tmp_dir/out-stop-1.json"
assert_is_empty_object "$tmp_dir/out-stop-1.json" "stop first response"

if [[ ! -f "$resume_path" ]]; then
  echo "assertion failed: expected RESUME file at $resume_path"
  exit 1
fi

if ! grep -Eq '^## Waypoint resume brief|^No active Waypoint task' "$resume_path"; then
  echo "assertion failed: RESUME.md does not contain expected header"
  exit 1
fi

echo "Step 5/6: run stop again (no new .cs edit)"
"${WAYPOINT_RUNNER[@]}" hook stop < "$tmp_dir/stop.json" > "$tmp_dir/out-stop-2.json"
assert_is_empty_object "$tmp_dir/out-stop-2.json" "stop second response"

echo "Step 6/6: verify at most one background index process is live"
max_bg="$(python3 - "$REPO_ROOT" <<'PY'
import subprocess
import sys
import time

repo = sys.argv[1]
needle = f"index --background --repo {repo} --quiet"
max_count = 0
for _ in range(30):
    proc = subprocess.run(["pgrep", "-f", needle], capture_output=True, text=True)
    if proc.returncode == 0 and proc.stdout.strip():
        count = len([line for line in proc.stdout.splitlines() if line.strip()])
    else:
        count = 0
    max_count = max(max_count, count)
    time.sleep(0.2)
print(max_count)
PY
)"

if [[ "$max_bg" -gt 1 ]]; then
  echo "assertion failed: expected at most one background index process, saw max=$max_bg"
  exit 1
fi

echo
echo "PASS: E3 / M6 checkpoint flow verified."
echo "  - preCompact writes checkpoint user_message"
echo "  - stop writes .cursor/waypoint/RESUME.md"
echo "  - background index process count stayed <= 1"
