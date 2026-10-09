#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURES_DIR="$ROOT_DIR/tests/fixtures/hooks"
WAYPOINT_RUNNER=(dotnet run --project "$ROOT_DIR/src/Waypoint.Cli/Waypoint.Cli.csproj" -p:NuGetAudit=false -- hook)

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

if [[ ! -f "$FIXTURES_DIR/beforeSubmitPrompt.json" ]]; then
  echo "error: missing fixture file: $FIXTURES_DIR/beforeSubmitPrompt.json"
  exit 1
fi

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/waypoint-m5-d3-XXXXXX")"
cleanup() {
  rm -rf "$tmp_dir"
}
trap cleanup EXIT

run_id="$(date +%s)-$$"

render_fixture() {
  local fixture_name="$1"
  local destination="$2"
  sed "s#{{REPO}}#$REPO_ROOT#g" "$FIXTURES_DIR/$fixture_name" > "$destination"
}

assert_has_additional_context() {
  local json_path="$1"
  local label="$2"
  python3 - "$json_path" "$label" <<'PY'
import json
import sys

path, label = sys.argv[1], sys.argv[2]
with open(path, "r", encoding="utf-8") as f:
    payload = json.load(f)

text = payload.get("additional_context")
if not isinstance(text, str) or not text.startswith("## Waypoint resume brief"):
    print(f"assertion failed ({label}): expected additional_context with Waypoint brief header", file=sys.stderr)
    sys.exit(1)
PY
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

echo "Using repo: $REPO_ROOT"
echo "Preparing hook payload fixtures in: $tmp_dir"

render_fixture beforeSubmitPrompt.json "$tmp_dir/beforeSubmitPrompt.json"
render_fixture afterAgentResponse.json "$tmp_dir/afterAgentResponse.json"
render_fixture sessionStart.json "$tmp_dir/sessionStart.json"
render_fixture postToolUse.read.json "$tmp_dir/postToolUse.read.json"
for f in "$tmp_dir"/*.json; do
  sed -i '' \
    -e "s/conv_demo_001/conv_demo_${run_id}/g" \
    -e "s/sess_demo_001/sess_demo_${run_id}/g" \
    "$f"
done

sed "s/\"session_id\": \"sess_demo_${run_id}\"/\"session_id\": \"sess_demo_fallback_${run_id}\"/" \
  "$tmp_dir/postToolUse.read.json" > "$tmp_dir/postToolUse.read.fallback.json"

echo "Step 1/5: create or link active task via beforeSubmitPrompt"
"${WAYPOINT_RUNNER[@]}" beforeSubmitPrompt < "$tmp_dir/beforeSubmitPrompt.json" > "$tmp_dir/out-beforeSubmitPrompt.json"

echo "Step 2/5: extract claims via afterAgentResponse"
"${WAYPOINT_RUNNER[@]}" afterAgentResponse < "$tmp_dir/afterAgentResponse.json" > "$tmp_dir/out-afterAgentResponse.json"

echo "Step 3/5: verify primary path (sessionStart injects brief)"
"${WAYPOINT_RUNNER[@]}" sessionStart < "$tmp_dir/sessionStart.json" > "$tmp_dir/out-sessionStart.json"
assert_has_additional_context "$tmp_dir/out-sessionStart.json" "sessionStart primary path"

echo "Step 4/5: verify fallback injects once on first postToolUse (fresh session)"
"${WAYPOINT_RUNNER[@]}" postToolUse --inject-brief-once < "$tmp_dir/postToolUse.read.fallback.json" > "$tmp_dir/out-postToolUse-1.json"
assert_has_additional_context "$tmp_dir/out-postToolUse-1.json" "postToolUse first fallback injection"

echo "Step 5/5: verify fallback does not inject again on second postToolUse"
"${WAYPOINT_RUNNER[@]}" postToolUse --inject-brief-once < "$tmp_dir/postToolUse.read.fallback.json" > "$tmp_dir/out-postToolUse-2.json"
assert_is_empty_object "$tmp_dir/out-postToolUse-2.json" "postToolUse second fallback call"

echo
echo "PASS: D3 / M5 brief wiring verified for both paths."
echo "  - sessionStart primary injection"
echo "  - one-time postToolUse fallback injection"
