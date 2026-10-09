#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
JQ_HELPER="$ROOT_DIR/scripts/bench-jq.sh"
RUNS_DIR=""
REPO=""
TAG="after-s1"
PROMPT=""
TESTS=""
ARMS="A B"
RUNS=3
MODEL=""
DEAD_END=""
OUT=""
YES=0
AGENT_BIN="${AGENT_CMD:-agent}"
PROMPT_TEXT=""
ARTIFACT_DIR=""

usage() {
  cat <<'USAGE'
Usage:
  BENCH_CONFIRM=1 scripts/bench.sh --repo <path> --prompt <file> --tests <project-or-sln> --model <model> --out <csv> [options]

Options:
  --repo <path>         Dedicated benchmark clone root (git repo).
  --tag <tag>           Session-1 checkpoint tag (default: after-s1).
  --prompt <file>       Prompt file used for Session 2.
  --tests <path>        dotnet test target.
  --arms "<list>"       Space-delimited arm list, e.g. "A B" or "A B B-empty".
  --runs <n>            Number of runs per arm (default: 3).
  --model <name>        Model passed through to agent CLI.
  --dead-end <pattern>  Pattern checked in edit/write tool calls.
  --out <file>          CSV output path.
  --yes                 Confirm destructive reset without BENCH_CONFIRM=1.
USAGE
}

fail() {
  echo "error: $*" >&2
  exit 1
}

now_epoch() {
  python3 - <<'PY'
import time
print(f"{time.time():.3f}")
PY
}

warn_user_level_waypoint_refs() {
  local home_cursor="$HOME/.cursor"
  [[ -d "$home_cursor" ]] || return 0

  local candidates=()
  [[ -f "$home_cursor/hooks.json" ]] && candidates+=("$home_cursor/hooks.json")
  [[ -f "$home_cursor/mcp.json" ]] && candidates+=("$home_cursor/mcp.json")
  if [[ -d "$home_cursor/rules" ]]; then
    while IFS= read -r line; do
      candidates+=("$line")
    done < <(rg --files "$home_cursor/rules")
  fi

  [[ "${#candidates[@]}" -gt 0 ]] || return 0

  if rg -n -i "waypoint" "${candidates[@]}" >/dev/null 2>&1; then
    echo "warning: user-level Cursor config in ~/.cursor references 'waypoint'."
    echo "warning: this can contaminate arm A; disable user-level Waypoint config before real runs."
  fi
}

require_clean_repo() {
  local dirty
  dirty="$(git -C "$REPO" status --porcelain)"
  [[ -z "$dirty" ]] || fail "working tree is dirty in $REPO; benchmark requires a clean tree."
}

verify_head_matches_tag() {
  local tag_commit head_commit
  tag_commit="$(git -C "$REPO" rev-parse "$TAG^{commit}")"
  head_commit="$(git -C "$REPO" rev-parse HEAD)"
  [[ "$head_commit" == "$tag_commit" ]] || fail "HEAD ($head_commit) differs from $TAG ($tag_commit). Refusing to continue."
}

restore_cursor_files() {
  local hooks="$REPO/.cursor/hooks.json"
  local mcp="$REPO/.cursor/mcp.json"
  local rule="$REPO/.cursor/rules/waypoint.mdc"

  if [[ -f "$hooks.off" && ! -f "$hooks" ]]; then mv "$hooks.off" "$hooks"; fi
  if [[ -f "$mcp.off" && ! -f "$mcp" ]]; then mv "$mcp.off" "$mcp"; fi
  if [[ -f "$rule.off" && ! -f "$rule" ]]; then mv "$rule.off" "$rule"; fi
  return 0
}

disable_waypoint_files() {
  local hooks="$REPO/.cursor/hooks.json"
  local mcp="$REPO/.cursor/mcp.json"
  local rule="$REPO/.cursor/rules/waypoint.mdc"

  if [[ -f "$hooks" ]]; then mv "$hooks" "$hooks.off"; fi
  if [[ -f "$mcp" ]]; then mv "$mcp" "$mcp.off"; fi
  if [[ -f "$rule" ]]; then mv "$rule" "$rule.off"; fi
  return 0
}

enable_waypoint_files() {
  restore_cursor_files
}

arm_setup() {
  local arm="$1"
  local graph_dir="$REPO/.git/waypoint"
  local graph_live="$graph_dir/graph.db"
  local graph_snapshot="$graph_dir/graph.$TAG.db"

  case "$arm" in
    A)
      disable_waypoint_files
      ;;
    B)
      enable_waypoint_files
      [[ -f "$graph_snapshot" ]] || fail "missing graph snapshot: $graph_snapshot"
      mkdir -p "$graph_dir"
      cp "$graph_snapshot" "$graph_live"
      rm -f "$graph_live-wal" "$graph_live-shm"
      ;;
    B-empty)
      enable_waypoint_files
      rm -f "$graph_live" "$graph_live-wal" "$graph_live-shm"
      ;;
    *)
      fail "unsupported arm '$arm' (supported: A, B, B-empty)"
      ;;
  esac
}

run_single() {
  local arm="$1"
  local run_number="$2"
  local ndjson_file test_log t0 t1 parse_output
  local input_tokens cache_read output_tokens tool_calls secs_to_first_edit tests_pass
  local tool_read tool_grep tool_edit tool_shell dotnet_build_test_runs wall_secs repeated_dead_end
  local ts

  echo "==> arm=$arm run=$run_number"
  git -C "$REPO" checkout "$TAG" -- .
  git -C "$REPO" clean -fd
  verify_head_matches_tag
  arm_setup "$arm"

  ndjson_file="$RUNS_DIR/$arm-$run_number.ndjson"
  test_log="$RUNS_DIR/$arm-$run_number.test.txt"

  t0="$(now_epoch)"
  (
    cd "$REPO"
    "$AGENT_BIN" -p --force --output-format stream-json --model "$MODEL" "$PROMPT_TEXT" \
      2>&1 | perl -MTime::HiRes=time -ne 'printf "%.3f\t%s", time, $_'
  ) > "$ndjson_file" || true
  t1="$(now_epoch)"

  if (cd "$REPO" && dotnet test "$TESTS" --no-restore) > "$test_log" 2>&1; then
    tests_pass=1
  else
    tests_pass=0
  fi

  parse_output="$("$JQ_HELPER" "$ndjson_file" "$t0" "$t1" "$DEAD_END")"
  input_tokens="$(printf "%s\n" "$parse_output" | awk -F= '$1=="input_tokens"{print $2}')"
  cache_read="$(printf "%s\n" "$parse_output" | awk -F= '$1=="cache_read"{print $2}')"
  output_tokens="$(printf "%s\n" "$parse_output" | awk -F= '$1=="output_tokens"{print $2}')"
  tool_calls="$(printf "%s\n" "$parse_output" | awk -F= '$1=="tool_calls"{print $2}')"
  secs_to_first_edit="$(printf "%s\n" "$parse_output" | awk -F= '$1=="secs_to_first_edit"{print $2}')"
  tool_read="$(printf "%s\n" "$parse_output" | awk -F= '$1=="tool_read"{print $2}')"
  tool_grep="$(printf "%s\n" "$parse_output" | awk -F= '$1=="tool_grep"{print $2}')"
  tool_edit="$(printf "%s\n" "$parse_output" | awk -F= '$1=="tool_edit"{print $2}')"
  tool_shell="$(printf "%s\n" "$parse_output" | awk -F= '$1=="tool_shell"{print $2}')"
  dotnet_build_test_runs="$(printf "%s\n" "$parse_output" | awk -F= '$1=="dotnet_build_test_runs"{print $2}')"
  wall_secs="$(printf "%s\n" "$parse_output" | awk -F= '$1=="wall_secs"{print $2}')"
  repeated_dead_end="$(printf "%s\n" "$parse_output" | awk -F= '$1=="repeated_dead_end"{print $2}')"

  verify_head_matches_tag
  ts="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  printf "%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n" \
    "$arm" "$run_number" "$input_tokens" "$cache_read" "$output_tokens" "$tool_calls" \
    "$secs_to_first_edit" "$tests_pass" "$tool_read" "$tool_grep" "$tool_edit" "$tool_shell" \
    "$dotnet_build_test_runs" "$wall_secs" "$repeated_dead_end" "$MODEL" "$ts" >> "$OUT"

  git -C "$REPO" checkout "$TAG" -- .
  git -C "$REPO" clean -fd
  verify_head_matches_tag
  require_clean_repo
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="$2"; shift 2 ;;
    --tag) TAG="$2"; shift 2 ;;
    --prompt) PROMPT="$2"; shift 2 ;;
    --tests) TESTS="$2"; shift 2 ;;
    --arms) ARMS="$2"; shift 2 ;;
    --runs) RUNS="$2"; shift 2 ;;
    --model) MODEL="$2"; shift 2 ;;
    --dead-end) DEAD_END="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --yes) YES=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) fail "unknown argument: $1" ;;
  esac
done

[[ -n "$REPO" ]] || fail "--repo is required"
[[ -n "$PROMPT" ]] || fail "--prompt is required"
[[ -n "$TESTS" ]] || fail "--tests is required"
[[ -n "$MODEL" ]] || fail "--model is required"
[[ -n "$OUT" ]] || fail "--out is required"
[[ -x "$JQ_HELPER" ]] || fail "missing executable helper: $JQ_HELPER"
[[ -f "$PROMPT" ]] || fail "prompt file not found: $PROMPT"
[[ -d "$REPO/.git" ]] || fail "repo is not a git checkout: $REPO"
command -v jq >/dev/null 2>&1 || fail "jq is required"
command -v perl >/dev/null 2>&1 || fail "perl is required"
command -v git >/dev/null 2>&1 || fail "git is required"
command -v dotnet >/dev/null 2>&1 || fail "dotnet is required"
command -v "$AGENT_BIN" >/dev/null 2>&1 || fail "agent command not found: $AGENT_BIN"

if [[ "${BENCH_CONFIRM:-0}" != "1" && "$YES" -ne 1 ]]; then
  fail "refusing destructive reset. Set BENCH_CONFIRM=1 or pass --yes."
fi

warn_user_level_waypoint_refs
require_clean_repo
verify_head_matches_tag
PROMPT_TEXT="$(cat "$PROMPT")"

FINAL_OUT_PATH="$OUT"
FINAL_RUNS_DIR="$(dirname "$FINAL_OUT_PATH")/runs"
ARTIFACT_DIR="$(mktemp -d "${TMPDIR:-/tmp}/waypoint-bench-XXXXXX")"
RUNS_DIR="$ARTIFACT_DIR/runs"
mkdir -p "$RUNS_DIR"
OUT="$ARTIFACT_DIR/results.csv"

if [[ ! -f "$OUT" || ! -s "$OUT" ]]; then
  printf "%s\n" \
    "arm,run,input_tokens,cache_read,output_tokens,tool_calls,secs_to_first_edit,tests_pass,tool_read,tool_grep,tool_edit,tool_shell,dotnet_build_test_runs,wall_secs,repeated_dead_end,model,ts" \
    > "$OUT"
fi
trap 'restore_cursor_files' EXIT

read -r -a ARMS_ARR <<< "$ARMS"

for ((i=1; i<=RUNS; i++)); do
  for arm in "${ARMS_ARR[@]}"; do
    run_single "$arm" "$i"
    restore_cursor_files
    require_clean_repo
  done
done

mkdir -p "$(dirname "$FINAL_OUT_PATH")"
cp "$OUT" "$FINAL_OUT_PATH"
rm -rf "$FINAL_RUNS_DIR"
mkdir -p "$FINAL_RUNS_DIR"
cp -R "$RUNS_DIR"/. "$FINAL_RUNS_DIR"/

echo "Done. Results: $FINAL_OUT_PATH"
echo "Run logs: $FINAL_RUNS_DIR"
