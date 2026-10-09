# Prior art: code graphs and agent memory (what already exists, and where the gap is)

Read this before pitching. The judges' first question will be "how is this different from X?"

## 1. Academic: repository-level code graphs for LLMs

| Work | What it does | Result | Relevance |
|---|---|---|---|
| **RepoGraph** (ICLR 2025) | Line-level repo graph; plug-in for SWE agents | +2.0–2.7% absolute on SWE-bench-Lite across RAG/Agentless/AutoCodeRover/SWE-agent; new open-source SOTA with Agentless (29.67%) | Proves structural graphs help *task success*, not just cost. [paper](https://proceedings.iclr.cc/paper_files/paper/2025/file/4a4a3c197deac042461c677219efd36c-Paper-Conference.pdf) |
| **CodexGraph** (NAACL 2025) | Code → graph DB; the LLM writes Cypher queries | Competitive on CrossCodeEval, SWE-bench, EvoCodeBench; 5 real-world apps | Agent-writes-graph-queries pattern; unified schema. [paper](https://aclanthology.org/2025.naacl-long.7/) |
| **GraphCoder** (ASE 2024) | Code Context Graph (control/data dependence) for coarse-to-fine retrieval | +6 EM on repo-level completion, less time/space than sequence RAG | Structure beats similarity for retrieval. [paper](https://dl.acm.org/doi/10.1145/3691620.3695054) |
| **Codebase-Memory** (arXiv 2603.27277, Mar 2026) | Tree-sitter KG in SQLite, 14–17 MCP tools, 66–162 languages, single C binary | 31 repos: 83% vs 92% quality, **10× fewer tokens, 2.1× fewer tool calls**; sub-ms queries | The most rigorous evidence for the KG layer. Also states the limitation: graph loses when full source context is needed → **hybrid** is right. [paper](https://arxiv.org/html/2603.27277v1) |
| **Zep / Graphiti** (arXiv 2501.13956) | Bitemporal KG for agent memory; episodes → entities/edges with validity windows | 94.8% DMR; +18.5% LongMemEval; −90% latency | The reference design for the *context graph* layer. [paper](https://arxiv.org/abs/2501.13956) |

## 2. Open-source tools already on the market

### Code knowledge graph MCP servers (the "what" layer)
- **codebase-memory-mcp** (DeusData) — the paper above, productised. Tree-sitter + hybrid LSP, SQLite, `search_graph`, `trace_call_path`, `get_architecture`, `detect_changes` (git diff → affected symbols with risk), `manage_adr` (persist Architecture Decision Records), Cypher via `query_graph`. Installs hooks for many clients. **For Cursor specifically it withholds context hooks: "session injection races and `readonly` blocks MCP."** [README](https://github.com/DeusData/codebase-memory-mcp)
- **code-graph-mcp** (sdsrss) — AST graph + BM25/vector hybrid search with RRF, L0–L3 token-aware compression, `project_map`, impact analysis. [repo](https://github.com/sdsrss/code-graph-mcp/)
- **context-mcp** (vibhasdutta) — persistent notes + "ContextGraph" of the codebase (tree-sitter, PageRank), `context.resume` loads recent entries at conversation start, `plan` auto-saves plans. [repo](https://github.com/vibhasdutta/context-mcp)

### Agent memory MCP servers (the "notes" layer)
- **apex-memory** — save decisions/patterns/bugs, importance scoring, link types (`supersedes`, `contradicts`, `depends_on`), cloud sync. [repo](https://github.com/jjerrychoi/apex-memory)
- **GraphMem-MCP** — per-project SQLite KG built "organically as the agent works," 28 tools, semantic + multi-hop. [repo](https://github.com/Sathvik-1007/graphrag-mcp)
- **mcp-neuralmemory / kg-memory** — Neo4j KG of goals, constraints, strategies/outcomes, preferences; `kg_autopilot` at task start; `kg_track_changes` links edits to goals. [repo](https://github.com/Hexecu/mcp-neuralmemory)
- **cursor-brain** — SQLite FTS5 + optional embeddings; relies on an `alwaysApply` rule to make the agent call `memory_search`/`memory_add`. [npm](https://www.npmjs.com/package/@samhithgardas/cursor-brain)
- **Tenure** (forum) — network-layer proxy that injects "beliefs" into every LLM payload across tools.

### The closest thing to the idea
- **memex** (STiFLeR7) — "bitemporal knowledge graph of your codebase — modules, symbols, **decisions, problems**." Daemon watches file + git events → Neo4j via Graphiti; Gemini Flash distils **commits into `Decision` nodes**; confidence decay; `memex review` TUI for validating low-confidence decisions. [repo](https://github.com/STiFLeR7/memex)
  - Overlap: KG + CG in one graph, decisions as first-class nodes, bitemporal.
  - Differences: decision granularity is the **commit**, not the **agent session**; capture is from git, not from harness telemetry (so uncommitted in-flight work and failed attempts are invisible); heavy stack (Neo4j + Gemini API + daemon); no auto-injection into the next Cursor session.

### Native / vendor
- **Cursor Memories** — sentence-sized facts, personal, not task state.
- **Anthropic memory tool** — file-based structured note-taking, model-driven.
- **Claude Code** — auto-memory files + compaction.

## 3. Gap analysis

Plotting the landscape on two axes:

```
                 Agent-driven (pull)                Harness-driven (push, automatic)
               ┌─────────────────────────────────┬────────────────────────────────────┐
 Structure     │ codebase-memory-mcp,            │ (Cursor Instant Grep index —       │
 ("what")      │ code-graph-mcp, RepoGraph,      │  lexical only, not a graph)        │
               │ CodexGraph                      │                                    │
               ├─────────────────────────────────┼────────────────────────────────────┤
 Work trace    │ apex-memory, GraphMem,          │ memex (from git commits, Neo4j)    │
 ("why/when")  │ cursor-brain, kg-memory,        │ Cursor Memories (facts only)       │
               │ context-mcp, manage_adr         │ Composer self-summary (in-session) │
               │                                 │                                    │
               │                                 │   ◄── GAP: hook-driven, session-   │
               │                                 │       granular, KG-linked, auto-   │
               │                                 │       injected at sessionStart,    │
               │                                 │       measured token savings       │
               └─────────────────────────────────┴────────────────────────────────────┘
```

**What nobody ships for Cursor today:**
1. **Automatic capture from the harness itself** (hooks), so the record is complete even when the agent never "decides" to remember, and includes failed attempts and in-flight uncommitted work.
2. **Session-granular decision traces linked to code entities** (a `Decision` about `Symbol X`, derived from `Episode Y`, in `Session Z`).
3. **Automatic re-injection** of a compact resume brief at the start of the next session (`sessionStart.additional_context`), with drill-down tools for the rest.
4. **A measured before/after** on the *second session* of a real multi-session task, using the harness's own token accounting.

Points 1 and 3 are precisely where the most mature competitor (codebase-memory) backed off for Cursor. If the hackathon MVP demonstrates that these work reliably, it has a differentiated story. If they don't, the fallback (rule + MCP `resume` tool) still works — see `findings/viability-analysis.md`.

## 4. What to borrow rather than build

- Tree-sitter parsing pipeline → use `tree-sitter` bindings directly (Python `tree-sitter` + `tree-sitter-language-pack`, or TS `web-tree-sitter`). Do not reimplement call resolution beyond same-file/import-resolved names; that's enough for the demo.
- Schema ideas → Codebase-Memory's node/edge types; Graphiti's episode/provenance/`superseded_by` pattern.
- Resume-brief structure → Composer's self-summary layout (Goal / Implemented / Errors-fixes / Still broken-next / Paths) plus "constraint clarity" and "action mapping" from the *Polished but Unresolved* paper.
- Optionally, run **codebase-memory-mcp** as the KG layer and build only the CG layer on top. Trade-off: faster, but the demo looks less "yours" and you inherit its Cursor-hook caveat. Recommended only if tree-sitter integration eats more than half a day.
