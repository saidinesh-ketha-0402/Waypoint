# Cursor integration surfaces (payload-level reference for the MVP)

Source: [cursor.com/docs/hooks](https://cursor.com/docs/hooks), [hooks.json schema guide](https://ntorres.dev/blog/cursor-hooks-json-guide), [Headless CLI](https://cursor.com/docs/cli/headless.md), [CLI output format](https://cursor.com/docs/cli/reference/output-format.md). Verified Sep 2026.

## 1. Hooks

### Configuration
`.cursor/hooks.json` (project) or `~/.cursor/hooks.json` (user). Command paths are relative to the `hooks.json` location. Each hook receives JSON on stdin and (for some events) returns JSON on stdout.

```json
{
  "version": 1,
  "hooks": {
    "sessionStart":        [{ "command": "uv run hooks/on_session_start.py" }],
    "beforeSubmitPrompt":  [{ "command": "uv run hooks/on_prompt.py" }],
    "postToolUse":         [{ "command": "uv run hooks/on_tool.py", "matcher": "Read|Grep|Shell|Write" }],
    "afterFileEdit":       [{ "command": "uv run hooks/on_edit.py" }],
    "afterAgentResponse":  [{ "command": "uv run hooks/on_response.py" }],
    "preCompact":          [{ "command": "uv run hooks/on_precompact.py" }],
    "stop":                [{ "command": "uv run hooks/on_stop.py" }],
    "sessionEnd":          [{ "command": "uv run hooks/on_session_end.py" }]
  }
}
```

### Common fields on every payload
`conversation_id` (stable across turns — this is our **Session** id), `generation_id` (changes per user message), `model`, `model_id`, `model_params`, `hook_event_name`, `cursor_version`, `workspace_roots[]`, `user_email`, **`transcript_path`** (full conversation transcript file, when transcripts are enabled). Env var `CURSOR_TRANSCRIPT_PATH` is also set.

### Events we care about

| Event | Input (key fields) | Output | Use in the MVP |
|---|---|---|---|
| `sessionStart` | `session_id`, `is_background_agent`, `composer_mode` | `{ "additional_context": "...", "env": {...} }` | **Inject the resume brief.** Fire-and-forget — the agent loop does not wait. **Race risk: test on day 1.** |
| `beforeSubmitPrompt` | `prompt`, `attachments[]` | `{ "continue": true }` | Record the user's goal/instructions as an `Episode(kind=prompt)`; first prompt of a new task creates the `Task`. Cannot inject context. |
| `postToolUse` | `tool_name`, `tool_input`, `tool_output` (JSON string), `duration` | `{ "additional_context": "..." }` | Record reads/greps/shell as `Episode`s; map file paths → KG nodes. **Can inject context after any tool result** → fallback injection point if `sessionStart` races. Matchers: `Shell`, `Read`, `Write`, `Grep`, `Delete`, `Task`, `MCP:<tool>`. |
| `afterFileEdit` | `file_path`, `edits[{old_string,new_string}]` | none | Record `Episode(kind=edit)`; re-parse the file incrementally; link edited `Symbol`s to the current `Task`. |
| `afterShellExecution` | command + output | none | Test runs, build results → `Episode(kind=shell)`; detect pass/fail for `Attempt` nodes. |
| `afterAgentResponse` | `text` | none | The agent's own narration. Extract `Decision` / `Constraint` / `OpenItem` candidates (regex first; optional cheap LLM). |
| `afterAgentThought` | `text`, `duration_ms` | none | Optional richer source for decision extraction (thinking text). |
| `preCompact` | `trigger`, `context_usage_percent`, `context_tokens`, `context_window_size`, `messages_to_compact`, `is_first_compaction` | `{ "user_message": "..." }` | **Checkpoint before loss:** run a full extraction pass over `transcript_path`; show the user "Waypoint checkpoint saved (N decisions, M open items)". |
| `stop` | `status` (`completed|aborted|error`), `loop_count` | `{ "followup_message": "..." }` | Final checkpoint; write session summary; record token usage for the benchmark. |
| `sessionEnd` | `session_id`, `reason`, `duration_ms`, `final_status` | none | Close the `Session` node. |
| `subagentStart`/`subagentStop` | subagent type, `parent_conversation_id`, `agent_transcript_path` | `followup_message` | Optional: attribute Explore-subagent reads to the parent task. |

### Where hooks run
Project hooks run in the IDE, the CLI, and cloud agents (except `sessionStart`/`sessionEnd`, MCP hooks and Tab hooks in cloud). This matters: **the CLI runs hooks**, so the benchmark harness exercises the same capture path as the IDE.

### Known caveat from the field
Codebase-Memory (most mature competitor) does not install Cursor context hooks: "session injection races and `readonly` blocks MCP." Treat `sessionStart.additional_context` reliability as **the #1 de-risking spike**. Fallbacks, in order:
1. `postToolUse.additional_context` on the *first* tool call of a session (inject once per `conversation_id`).
2. `alwaysApply` rule: "Before any exploration, call `waypoint.resume()`." (agent-pull, but rules are honoured reliably).
3. `stop` hook regenerates `.cursor/waypoint/RESUME.md`; rule tells the agent to read it first.

## 2. MCP server

`.cursor/mcp.json`:
```json
{ "mcpServers": { "waypoint": { "command": "uv", "args": ["run", "waypoint", "serve"] } } }
```
Stdio transport, local process, no network. Cursor loads MCP tool definitions dynamically (only when relevant), so a handful of well-named tools costs little. Keep tool count small (5–7) and descriptions crisp; the agent picks tools by description.

## 3. Rules

`.cursor/rules/waypoint.mdc` with `alwaysApply: true`:
> At the start of a conversation in this repo, if no "Waypoint resume brief" is present in context, call `waypoint.resume` before exploring. When you make a design decision or discover a constraint, call `waypoint.record_decision`. Before saying a task is complete, call `waypoint.open_items` and address or list them.

This is the reliability backstop and also nudges explicit, high-quality decision records.

## 4. CLI (for the benchmark)

```bash
agent -p --force --output-format json "<prompt>"          # single JSON with final result + usage
agent -p --force --output-format stream-json "<prompt>"   # NDJSON per assistant message / tool event
```
The final `result` event includes `usage: { inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens }` (confirmed in CLI 2026.09.x). `stream-json` gives tool-call events to count tool calls and time-to-first-edit. `--force` allows file writes. Project hooks fire in CLI runs, so Waypoint capture works headlessly.

A/B protocol: run the same Session-2 prompt with hooks/MCP enabled (arm B) and with `.cursor/hooks.json` + `mcp.json` temporarily moved aside (arm A). Same model, same repo state (reset via `git stash`/`git checkout` between runs).

## 5. Transcript files

`transcript_path` points to the full conversation text. For the `preCompact` and `stop` checkpoints, parse it to extract decisions rather than relying only on the incremental `afterAgentResponse` stream; it is the highest-fidelity source and includes tool results the agent reasoned over.
