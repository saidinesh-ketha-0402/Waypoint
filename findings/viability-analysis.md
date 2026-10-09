# Findings: is the idea viable, and in what form?

**Verdict: viable, with a sharpened scope.** The problem is real and acknowledged by Cursor, researchers and practitioners. The generic version of the idea ("a knowledge graph of the code for the agent") is already built by several teams and would not stand out. The specific version — **a hook-driven context graph of the agent's own work, linked to a code knowledge graph, auto-injected into the next session, with measured token savings** — is not shipped by anyone for Cursor and is buildable in a hackathon. Working name used below: **Waypoint**.

---

## 1. Assumption check

| Your assumption | Status | What the evidence says |
|---|---|---|
| Cursor gathers context via semantic search, going through files one by one | **Outdated / partially wrong** | Semantic embedding search was turned down in 2026. The agent now uses a local lexical "Instant Grep" index + targeted `Read`s + an Explore subagent, deciding at runtime what to search. It does not sweep files; it iterates grep→read→grep. Each iteration re-derives structure (callers, dependencies) that is never persisted. (`research/01`) |
| In the last ~25% of the window the agent gets less intelligent and drifts | **Right in spirit, wrong in shape** | Degradation is a slope, not a cliff, and begins earlier. It shows up as *premature termination* and *"polished but unresolved"* submissions — exactly the "deviates from its plan" symptom you describe. Cursor mitigates in-session via compaction/self-summary, which is itself lossy. (`research/03`) |
| Switching sessions throws away all gathered context | **Correct** | Nothing native carries task state across conversations. Memories are one-line facts; rules are static; transcripts are not queryable or injected. Cursor's own Composer 2 report lists "longer-term memory architectures" as future work. |
| Re-gathering is expensive in tokens | **Correct, and quantified** | Exploration dominates input-token cost; structural questions cost ~10k tokens via file exploration vs ~1k via a graph (Codebase-Memory, 31 repos). Session N+1 re-orientation is the avoidable part. |
| KG = "what exists and how linked"; CG = "why did this change, under what rules" | **Correct** | Matches TrustGraph, contextgraph.tech, Atlan, Graphiti and the Aug-2026 white paper. CG = KG + time + provenance + scope + reified decisions. (`research/02`) |
| Building the graphs has a high upfront cost that later savings must amortise | **Overstated** | The structural KG is built deterministically by tree-sitter in seconds, with zero LLM tokens. The CG is captured from hook payloads the harness already emits. The only optional LLM cost is decision extraction from the agent's prose, which can be regex-first and use a cheap model. Upfront cost is small; the trade-off you feared mostly does not exist. |
| "I can't figure out how to wire this into Cursor" | **Solved** | Cursor Hooks (capture + `sessionStart.additional_context` injection), MCP (query tools), Rules (reliability backstop), CLI `--output-format json` (token measurement). (`research/05`) |

---

## 2. What is already solved (do not rebuild, do not pitch as novel)

- **Code KG over MCP:** codebase-memory-mcp (peer-reviewed, 162 languages), code-graph-mcp, context-mcp. Judges who know the space will know these.
- **Agent memory over MCP:** apex-memory, GraphMem, cursor-brain, kg-memory. All are *pull-based* — the agent has to remember to call `memory_add`, and the ecosystem admits this is unreliable.
- **In-session compaction:** Cursor and Anthropic both ship it; you cannot improve on it from outside.
- **Commit-level decision graphs:** memex does this with Neo4j + Gemini.

If the pitch is "we built a knowledge graph of the codebase so the agent uses fewer tokens," the reply will be "Codebase-Memory published that in March." Avoid.

---

## 3. The gap (what to pitch)

Four things no shipping tool does together for Cursor:

1. **Push, not pull.** Capture comes from the harness's lifecycle events (every read, grep, edit, shell run, agent response, compaction, stop), so the record is complete even when the agent never "decides" to remember — and it includes **failed attempts** and **uncommitted in-flight work**, which git-based approaches miss.
2. **Session-granular, KG-linked decisions.** `Decision → ABOUT → Symbol`, `Decision → DERIVED_FROM → Episode`, `Decision → SUPERSEDES → Decision`. The next session can ask *why* a function looks the way it does and get the reasoning plus the structural neighbourhood.
3. **Automatic re-injection.** A ≤1,500-token resume brief lands in the new conversation before the first user turn, structured the way Composer's own self-summaries are (goal / done / decisions & constraints / failed attempts / open items & next steps / hot files). Drill-down via MCP tools.
4. **Measured on the harness's own meter.** Cursor CLI reports `inputTokens/cacheReadTokens` per run; a two-session task run with and without Waypoint gives a number judges can believe.

Point 1 and 3 are exactly where the most mature competitor (Codebase-Memory) backed off for Cursor ("session injection races"). Demonstrating they work is the differentiator; if they don't, the fallbacks still deliver most of the value (see §5).

---

## 4. Why this should interest Trimble engineering and management

- **Direct developer-productivity ROI.** Trimble maintains large, old codebases (Tekla Structures, SketchUp, Trimble Business Center — C++/.NET, decades of history). Multi-session agent tasks on such repos are the norm, not the exception. Re-orientation cost scales with repo size and age.
- **Cost transparency.** Token spend on agentic coding is a line item now. A tool that shows "session 2 used 42% fewer input tokens and finished the task" speaks to both the engineering and finance side.
- **The same pattern generalises to Trimble's own agent platform.** Trimble's Agentic AI platform / Agent Studio runs agents over BIM models, project data and field workflows. Those agents have the same problem (long tasks, lost context, no audit trail of *why* an agent did something). A context graph with provenance is also an **audit artefact** — attractive in a regulated, liability-heavy industry (construction, surveying). Pitch the coding-agent MVP as the proving ground for a pattern the platform team can adopt.
- **Low adoption friction.** Project-level `.cursor/` config, local SQLite, no server, no API keys, no code leaves the machine. Easy to pilot on one team.

---

## 5. Risks and mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| `sessionStart.additional_context` races / is dropped (fire-and-forget) | Medium — a competitor cites this | **Day-1 spike.** Fallbacks: inject on first `postToolUse` per conversation; `alwaysApply` rule instructing the agent to call `waypoint.resume`; `stop` hook writes `RESUME.md`. Any one of these preserves the demo. |
| Decision extraction from agent prose is noisy | High | Regex/heuristic first (sentences with "because", "instead of", "decided", "constraint", "TODO", "blocked", test pass/fail from shell output). Optional one cheap LLM call at `preCompact`/`stop` only. Show provenance links so noisy items are inspectable, not trusted blindly. Add `record_decision` MCP tool + rule to get high-quality explicit records. |
| The resume brief is too long / too short | Medium | Hard budget (≤1,500 tokens); rank items by recency × touch-count × "open" status; put pointers to MCP drill-down tools for the rest. |
| Token savings smaller than hoped | Medium | Frame as hypothesis; report tool calls, time-to-first-edit and task success alongside tokens. Even a 25% reduction with equal success is a strong result; also show *quality* wins (no repeated failed attempt, no scope drift). |
| Tree-sitter call resolution is weak for C++/C# (Trimble's languages) | High for full accuracy, low for the demo | Demo on a Python or TypeScript repo. State clearly that the KG layer can be swapped for codebase-memory-mcp (hybrid LSP) for C++/C# in production. |
| Hackathon rules forbid pre-built code | Unknown | Do spikes and throwaway experiments before; keep real code for the event. Check the rules. |
| Scope creep into a full temporal graph DB | High for a hackathon | SQLite, ~8 tables, bitemporal-lite (`created_at`, `superseded_by`, `session_id`). No Neo4j. |

---

## 6. Alternatives if you want to de-scope

Ordered from most to least ambitious; each is a valid hackathon deliverable on its own.

1. **Full Waypoint (recommended):** KG + CG + hooks + MCP + sessionStart injection + benchmark + graph visual.
2. **CG-only:** skip tree-sitter; link decisions to *file paths* rather than symbols. Loses "impact" queries and the pretty call graph, keeps 80% of the resume value. Fall back to this if tree-sitter costs more than half a day.
3. **Structured handoff only:** `stop`/`preCompact` hooks generate a typed `RESUME.md`; a rule makes the next session read it. No graph, no MCP. This is roughly what the `/handoff` skill in your own Cursor setup does manually — it is the floor, not the pitch.
4. **Reuse codebase-memory-mcp for the KG layer** and build only the CG + injection on top. Fastest path to a working demo; weaker "we built this" story; inherits its Cursor-hook caveat.

---

## 7. Bottom line

- The problem statement is accurate once you swap "semantic search" for "grep-first agentic retrieval."
- Your KG/CG framing is correct and current; the CG layer is where the novelty is.
- Cursor exposes every integration point you need; the one uncertainty (`sessionStart` injection) has three fallbacks.
- The upfront-cost concern is largely moot: structure is free (tree-sitter), trace is free (hooks), summarisation is cheap.
- A convincing MVP is: two sessions, one real task, one number ("session 2: −X% input tokens, −Y% tool calls, task still passes"), one graph picture showing *why* a change was made. See `idea/`.
