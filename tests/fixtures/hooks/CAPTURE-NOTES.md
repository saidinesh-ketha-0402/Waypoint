# Hook Fixture Capture Notes

- These files follow the event field shapes documented in `research/05-cursor-integration-surfaces.md`.
- The `postToolUse.shell.*` payloads now embed real command output snippets captured during local fixture generation.
- End-to-end raw Cursor hook payload dumps from an IDE/CLI spike are not yet available in this repo; when they are captured, replace the remaining synthetic envelope values (`conversation_id`, `session_id`, etc.) verbatim from those captures.
