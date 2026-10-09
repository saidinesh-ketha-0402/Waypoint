#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "Usage: $0 <stream-json.ndjson> <t0> <t1> [dead-end-pattern]" >&2
  exit 1
fi

STREAM_FILE="$1"
T0="$2"
T1="$3"
DEAD_END="${4:-}"

jq -nr \
  --rawfile raw "$STREAM_FILE" \
  --argjson t0 "$T0" \
  --argjson t1 "$T1" \
  --arg deadEnd "$DEAD_END" '
def lines:
  ($raw | split("\n") | map(select(length > 0)));

def parse($line):
  ($line | index("\t")) as $i
  | if $i == null then
      { ts: null, json: "", evt: null }
    else
      {
        ts: (try ($line[0:$i] | tonumber) catch null),
        json: ($line[$i + 1:] // ""),
        evt: (try (($line[$i + 1:] // "{}") | fromjson) catch null)
      }
    end;

def event_type($e):
  ($e.evt.type // "" | tostring | ascii_downcase);

def tool_name($e):
  (
    $e.evt.tool_name
    // $e.evt.name
    // $e.evt.tool.name
    // $e.evt.tool_call.name
    // $e.evt.toolCall.name
    // ""
  )
  | tostring
  | ascii_downcase;

def tool_input($e):
  (
    $e.evt.tool_input
    // $e.evt.input
    // $e.evt.arguments
    // $e.evt.toolCall.arguments
    // $e.evt.tool_call.arguments
    // {}
  );

def is_tool_event($e):
  (event_type($e) | test("tool\\.(call|invocation)|tool_call|toolcall|tool-use|tool_use"))
  or ((tool_name($e) | length) > 0 and (event_type($e) | startswith("tool")));

def is_result_event($e):
  event_type($e) == "result";

def kind($e):
  (tool_name($e)) as $n
  | if ($n | test("read")) then "read"
    elif ($n | test("grep|glob")) then "grep"
    elif ($n | test("edit|write|applypatch|create_or_update_file|delete_file")) then "edit"
    elif ($n | test("shell|bash|terminal")) then "shell"
    else "other"
    end;

def shell_cmd($e):
  (
    tool_input($e).command
    // tool_input($e).cmd
    // tool_input($e).shell_command
    // ""
  )
  | tostring;

def dead_end_in_edit($e):
  ($deadEnd | length) > 0
  and kind($e) == "edit"
  and ((tool_input($e) | tostring) | contains($deadEnd));

(lines | map(parse(.))) as $events
| ($events | map(select(.evt != null))) as $valid
| ($valid | map(select(is_result_event(.))) | last // {}) as $last_result
| ($last_result.evt.usage // {}) as $usage
| ($valid | map(select(is_tool_event(.)))) as $tools
| ($tools | map(select(kind(.) == "read")) | length) as $tool_read
| ($tools | map(select(kind(.) == "grep")) | length) as $tool_grep
| ($tools | map(select(kind(.) == "edit")) | length) as $tool_edit
| ($tools | map(select(kind(.) == "shell")) | length) as $tool_shell
| ($tools | length) as $tool_calls
| ($tools | map(select(kind(.) == "edit")) | map(.ts) | map(select(. != null)) | first // null) as $first_edit_ts
| ($tools | map(select(kind(.) == "shell" and (shell_cmd(.) | test("dotnet\\s+(build|test)")))) | length) as $dotnet_runs
| ($tools | any(dead_end_in_edit(.))) as $dead_end_hit
| "input_tokens=\($usage.inputTokens // $usage.input_tokens // 0)",
  "cache_read=\($usage.cacheReadTokens // $usage.cache_read_tokens // 0)",
  "output_tokens=\($usage.outputTokens // $usage.output_tokens // 0)",
  "tool_calls=\($tool_calls)",
  "secs_to_first_edit=\(if $first_edit_ts == null then "" else (($first_edit_ts - $t0) | tostring) end)",
  "tool_read=\($tool_read)",
  "tool_grep=\($tool_grep)",
  "tool_edit=\($tool_edit)",
  "tool_shell=\($tool_shell)",
  "dotnet_build_test_runs=\($dotnet_runs)",
  "wall_secs=\(($t1 - $t0) | tostring)",
  "repeated_dead_end=\(if $dead_end_hit then 1 else 0 end)"
'
