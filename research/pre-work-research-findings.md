# Waypoint — Pre-work research findings

**Purpose:** Consolidated record of research conducted before proposing Waypoint. This document contains **findings only** (what we learned from literature, product behavior, prior art, and integration probes)—not implementation specs or build plans.

**Source notes:** Detailed memos live in `research/01`–`05`; bibliography in `research/sources.md`. Assumption validation draws from `findings/viability-analysis.md` §1.

**As-of:** Research captured September 2026 unless a source states otherwise.

---

## Executive summary (findings)

1. **Cursor retrieval is grep-first and just-in-time**, not embedding-first. Agents re-derive structure through repeated grep→read cycles; that work is **not persisted** across turns or sessions.
2. **Context degradation is real, gradual, and early-onset**—manifesting as premature termination and polished-but-incomplete work, not only “running out of window.” **Compaction and self-summarization are lossy** by vendor and researcher admission.
3. **Native cross-session task state is thin:** rules are static, memories are sentence-sized facts, transcripts are not queryable or auto-injected. **Pull-based memory MCPs are widely regarded as unreliable** because the agent must remember to call them.
4. **Knowledge graphs (structure) and context graphs (work trace + provenance)** are distinct in the literature; for **coding agents**, harness telemetry makes context capture unusually complete compared to general enterprise settings.
5. **Code graphs materially reduce exploration cost:** rigorous work shows ~**10×** fewer tokens and ~**2.1×** fewer tool calls for structural questions vs file exploration, with trade-offs when full source text is required.
6. **Prior art covers “what” (code KG) and pull-based “notes,”** but **no shipping Cursor integration** combines harness-driven capture, session-granular KG-linked decisions, automatic session-start injection, and measured Session-2 token savings.
7. **Cursor exposes sufficient integration surfaces** (hooks, MCP, rules, CLI usage metrics) to observe behavior and measure before/after— with a **documented risk** that `sessionStart` context injection may race.

---

## 1. How Cursor gathers and manages context

### 1.1 Retrieval mechanism (2026)

- Cursor **deprioritized semantic/embedding codebase indexing** (reported mid-2026, v3.12+) in favor of a local **“Instant Grep”** index plus targeted **Read** and other tools—the agent acts as retriever (**just-in-time context**).
- Instant Grep index: **per-repo, local**, under `.git/cursor/`, git-anchored, layered with uncommitted edits; respects `.gitignore` / `.cursorignore`; falls back to ripgrep without an index.
- Agents use **Grep, Read, Glob, Shell** and may delegate to an **Explore subagent** in an isolated window so exploration does not bloat the parent context.
- **Industry convergence:** major coding agents (Claude Code, Cursor, Windsurf, Cline, Amp, etc.) moved toward agentic search over static RAG/embeddings by 2026.

**Finding:** The common belief that “Cursor semantically indexes and walks the codebase” is **outdated**. Exploration cost comes from **iterative re-discovery of structure** (callers, boundaries, dependencies), not from a persistent structural model in the harness.

### 1.2 In-session context management

- **Compaction** when context limits are approached: history is shortened via summarization or sliding window. Cursor documents that compaction **can cause the model to forget critical information**.
- **Composer self-summarization:** RL-trained models pause at token triggers, summarize (~1k tokens in reported setups), continue; measured as **reducing compaction error** vs prompt-based compaction—not eliminating information loss.
- Real long-horizon self-summaries follow a **task-state shape:** goal → implemented components → errors/fixes → still broken / next steps → paths—i.e. prose handoff, not a queryable graph.
- **`preCompact` hook** exposes `context_usage_percent`, `context_tokens`, `context_window_size`, `messages_to_compact`, `is_first_compaction`—compaction is **observable** before loss.
- Other harness efficiencies (2026): dynamic MCP tool loading (~47% token reduction in MCP-heavy sessions reported), prompt-cache breakpoints, subagent delegation—**none persist knowledge across conversations**.

**Finding:** Cursor mitigates degradation **within a session**; it does **not** natively maintain a faithful, queryable record of work **across sessions**.

### 1.3 Cross-session persistence (native)

| Mechanism | Holds | Team-shared | Carries task state (done / failed / left)? |
|-----------|--------|-------------|---------------------------------------------|
| Rules (`.cursor/rules`, `AGENTS.md`) | Static instructions | Yes (in repo) | No |
| Memories | One-sentence facts | No (per user) | No |
| Skills | Procedures | Varies | No |
| Chat history / transcripts | Full text | No | Implicit only; not injected or structured |
| Plan mode | Plan document | Optional | Partial |

**Finding:** Ecosystem commentary consistently treats **agent-initiated memory tools as unreliable**; workflows are often encoded in rules because the server cannot force timely memory calls.

### 1.4 Integration surfaces (existence, not product design)

- **Hooks:** 20+ lifecycle events; payloads include `conversation_id`, `generation_id`, `workspace_roots`, `transcript_path`. Events include `sessionStart` (can return `additional_context`), tool/edit/shell/response events, `preCompact`, `stop`, `sessionEnd`.
- **MCP:** stdio servers; tool definitions loaded dynamically to limit token overhead.
- **CLI:** headless runs can emit **per-run token usage** (`inputTokens`, `outputTokens`, cache read/write)—usable for controlled A/B measurement.
- **Hooks in CLI:** project hooks run in IDE and CLI (with documented exceptions for some cloud agent modes)—benchmarks can exercise the same capture path as the IDE.

**Finding:** The harness is **observable and partially injectable**; cross-session continuity is a **gap in product**, not a gap in available APIs.

---

## 2. Context degradation and long-horizon behavior

### 2.1 “Context rot” and performance vs length

- **Chroma (*Context Rot*):** Across many models, performance grows **increasingly unreliable as input length grows**, even on trivial tasks; worse when relevant information is semantically distant from the query.
- **arXiv 2606.29718:** Long-horizon search shows **premature termination** correlated with accumulated context length; context-management methods help partly by reducing early give-up.
- **“Context length alone hurts” (summarized in practitioner literature):** Large performance drops with input length **even under perfect retrieval**—length itself is a stressor.
- **Long-Horizon-Terminal-Bench (arXiv 2607.08964):** Failures include error accumulation over long trajectories; authors call for **stronger memory and progress tracking**.
- **Polished but Unresolved (arXiv 2609.00823):** Late-stage agents produce **complete-looking but constraint-violating** outputs; mitigations that work include **explicit unresolved-constraint lists** and **ordered next-step plans**.

**Finding:** Degradation is a **slope**, not a cliff at “75% full.” Symptoms include **scope drift, premature “done,” and unresolved constraints**—not only obvious mistakes.

### 2.2 Compaction and summarization

- **Cursor:** Compaction can drop critical information; self-summarization improves on generic compaction but is still a **lossy** transform.
- **Anthropic:** Compaction described as **lossy by design**; aggressive compaction loses subtle context whose importance appears later.

**Finding:** Summaries alone are an **incomplete archive**; durable typed traces (if captured) can support regeneration and drill-down.

### 2.3 Re-orientation and token economics

- **Codebase-Memory (arXiv 2603.27277):** Agentic exploration often requires **many tool calls and hundreds of thousands of input tokens** before answering structural questions; **input tokens dominate cost** even with caching.
- Same study (31 repos): graph-backed answers used **~1,000 tokens vs ~10,000** for file exploration (~**10×**), **~2.1× fewer tool calls**, ~**90%** of explorer quality on evaluated questions. Graphs weak when queries need **full source** or exhaustive pattern match → **hybrid** optimal.
- Vendor/marketing claims elsewhere cite higher ratios (e.g. 75–95% fewer tool calls); treat as **directional**, not audited.
- **LoCoBench-Agent (cited in Codebase-Memory):** Comprehension efficiency trades off with thorough exploration; **pre-materialized structure shifts the Pareto frontier**.

**Finding:** The largest defensible savings target for multi-session work is **skipping re-discovery in session N+1**, not eliminating all file reads. A **30–50%** input-token reduction on continuation sessions is a **reasonable hypothesis to measure**, not an established constant.

### 2.4 Cross-session amnesia (ecosystem)

- Memory MCP projects repeatedly state the same problem: agents **forget between sessions**, re-read files, repeat mistakes.
- Practitioner forums: **no widely accepted production-grade** cross-tool context setup.
- **Composer 2** materials list **longer-term memory architectures** as future work.

**Finding:** Cross-session continuity is **acknowledged by vendors, researchers, and practitioners** and remains **largely unsolved** in shipping Cursor workflows.

---

## 3. Knowledge graphs vs context graphs

### 3.1 Definitions (literature alignment)

- **Knowledge graph:** *What exists and how it is linked*—entities and typed relationships; strong for multi-hop structure; typically a **snapshot of now**.
- **Context graph:** KG extended with **time**, **provenance**, **situational scope** (task/user/branch), and **reified decisions/events**—optimized for *why, when, under what rules, from what evidence*.
- Sources converging on this split include TrustGraph, contextgraph.tech, Atlan, Majmundar (Zenodo, Aug 2026), and temporal frameworks such as **Graphiti (Zep)**.

**Finding:** The project’s KG/CG framing **matches 2026 vendor and academic taxonomy**.

### 3.2 Enterprise skepticism vs coding agents

- **Verdantix:** Context graphs may be incremental hype because much decision context is **never recorded digitally** in typical enterprises.

**Finding:** For **coding agents**, decisions are **already digital** (prompts, tool I/O, edits, test output, narration) and **hooks emit them as JSON**—the capture problem is **far more tractable** than in general BPM/enterprise knowledge work.

### 3.3 Temporal / provenance patterns (reference implementations)

- **Graphiti:** Episodes as raw inputs; entities/edges trace to episodes; **bitemporal** validity; contradictions **invalidate** rather than delete; incremental updates; reported strong results on memory benchmarks vs full-context baselines.

**Finding:** Full bitemporal engines are not required to learn from the space; minimal timestamps, `session_id`, and `superseded_by` are sufficient to represent **current vs historical belief** in prototypes.

### 3.4 Code as knowledge graph

- Deterministic parsers (e.g. tree-sitter; Roslyn for C#) can build large code graphs in **seconds** with **zero LLM tokens** for the structure layer (Codebase-Memory reports tens of thousands of nodes in single-digit seconds on mid-size repos).

**Finding:** The assumption that “building a code graph is prohibitively expensive upfront” is **false for the structure layer**; cost is dominated by **agent exploration**, not deterministic indexing.

### 3.5 Structured memory taxonomy (Anthropic)

- Context management strategies include compaction, tool-result clearing, and **structured note-taking** (external memory read back later)—with reported gains when memory and editing combine.

**Finding:** A typed, harness-written trace with schema and provenance is **structured note-taking without relying on the model to decide when to write**.

---

## 4. Prior art and landscape gap

### 4.1 Academic evidence (selected)

| Work | Key result relevant to Waypoint problem |
|------|----------------------------------------|
| **RepoGraph (ICLR 2025)** | Repo graphs improve SWE-agent outcomes on benchmarks—not only cost. |
| **CodexGraph (NAACL 2025)** | Graph query interfaces for code; strong on multiple code tasks. |
| **GraphCoder (ASE 2024)** | Structural/code-context graphs beat sequence RAG for repo-level completion. |
| **Codebase-Memory (arXiv 2603.27277)** | Rigorous token/tool-call savings for structural queries; hybrid needed for source-heavy questions. |
| **Zep / Graphiti (arXiv 2501.13956)** | Temporal KG memory with episode provenance; strong benchmark latency/accuracy claims. |

### 4.2 Open-source products (categories)

- **Code KG MCP servers** (codebase-memory-mcp, code-graph-mcp, context-mcp): strong on **structure** and pull-based query; vary on ADRs, resume, plans.
- **Agent memory MCPs** (apex-memory, GraphMem, kg-memory, cursor-brain, etc.): **pull-based**; depend on agent calling tools; link types and scoring vary.
- **memex:** Closest combined KG + decisions graph—但 decisions primarily from **commits** via LLM distillation; **Neo4j + daemon + API**; misses **in-flight uncommitted work and failed attempts**; not Cursor session telemetry.

### 4.3 Competitor constraint on Cursor

- **codebase-memory-mcp** explicitly **withholds Cursor context hooks**, citing **session injection races** and **`readonly` blocking MCP** in some setups.

**Finding:** The most mature code-graph competitor **does not claim** reliable automatic session injection for Cursor—this is an **integration risk area**, not a solved industry standard.

### 4.4 Gap matrix (research conclusion)

Landscape axes: **structure (“what”) vs work trace (“why/when”)** × **pull (agent-driven) vs push (harness-driven)**.

**Observed occupancy:**

- **Structure + pull:** codebase-memory, code-graph-mcp, academic repo graphs.
- **Structure + push:** Cursor Instant Grep (lexical index, **not** a semantic/call graph).
- **Work trace + pull:** most memory MCPs, context-mcp, ADR tools.
- **Work trace + push (partial):** memex (git/commit granularity), Composer self-summary (**in-session only**), Cursor Memories (**facts, not tasks**).

**Research gap (no shipped Cursor product documented as doing all of):**

1. **Automatic harness capture** (complete when agent never calls memory tools)—including **failed attempts** and **uncommitted in-flight work**.
2. **Session-granular decision traces linked to code entities** (symbol/file level with provenance to episodes).
3. **Automatic re-injection** at session boundary (e.g. `sessionStart.additional_context`) plus drill-down tools.
4. **Measured Session-2 A/B** on a multi-session task using the harness’s own token accounting.

**Finding:** Pitching “we built a code knowledge graph” alone **collides with mature prior art**; the differentiated research gap is **harness-linked context graph + push continuity + measurement**.

---

## 5. Cursor integration research (capabilities and risks)

### 5.1 Hook capabilities confirmed in docs (Sep 2026)

- Stable **`conversation_id`** across turns suitable as session key.
- **`transcript_path`** for high-fidelity checkpoint parsing at compaction/stop.
- **`sessionStart` → `additional_context`:** documented injection point; described as **fire-and-forget** with **race risk** (must be spike-tested).
- **`postToolUse` → `additional_context`:** documented fallback injection after tool results.
- **`preCompact` / `stop`:** suitable checkpoint boundaries before summarization loss.
- Tool events cover Read/Grep/Shell/Write/MCP; edits and shell output available for structured pass/fail signals.

### 5.2 Measurement instrument

- CLI **`agent -p --force --output-format json | stream-json`** exposes final **usage** and per-event tool streams for counting tool calls and time-to-first-edit.

**Finding:** A rigorous **Session-1 setup / Session-2 continuation A/B** protocol is ** feasible** with the same repo state and hook paths in headless runs.

### 5.3 Documented integration risks (field + competitor)

- **`sessionStart` injection reliability** is the **primary de-risking unknown** (competitor explicitly avoided Cursor hooks for this reason).
- Documented fallback mechanisms exist in the ecosystem (first `postToolUse` injection, always-on rules, file-based resume)—research records them as **mitigations to validate**, not as proven defaults.

---

## 6. Assumption validation (pre-proposal)

| Initial assumption | Research verdict |
|--------------------|------------------|
| Cursor uses semantic search file-by-file | **Outdated / partial:** grep-first, JIT retrieval; structure re-derived per session. |
| Agent gets “dumb” in the last ~25% of window | **Partially true, wrong shape:** early slope; premature termination and polished incomplete outputs. |
| New session loses gathered context | **True:** no native task-state injection. |
| Re-gathering is token-expensive | **True, quantified:** exploration dominates; ~10× structural query gap in rigorous study. |
| KG vs CG distinction | **Correct** per 2026 literature. |
| Graph build cost blocks ROI | **Overstated for KG layer:** deterministic parse is cheap; LLM optional for prose extraction only. |
| Cannot wire into Cursor | **False:** hooks, MCP, rules, CLI metrics exist; injection reliability must be tested. |

---

## 7. Trimble-relevant context (external signals only)

- Public reporting from **Trimble Dimensions 2025** emphasizes **agentic AI** and platform-scale agent tooling (Agent Studio direction).
- Trimble software lines (e.g. Tekla 2026 messaging) reference **AI-assisted workflows** in engineering/construction software—long-horizon, liability-sensitive domains where **auditability of automated decisions** is valued.

**Finding:** Trimble’s stated direction toward **long-running agents on complex domain data** shares the **same continuity and provenance problem shape** as multi-session coding agents—coding repos are a **constrained proving ground**, not a separate problem category.

---

## References

Full URL list and access date: **`research/sources.md`**.

Primary research memos:

| Memo | Topic |
|------|--------|
| `research/01-how-cursor-gathers-context.md` | Cursor retrieval, compaction, cross-session gaps |
| `research/02-knowledge-graphs-vs-context-graphs.md` | KG/CG definitions, temporal patterns, coding mapping |
| `research/03-context-degradation-and-long-horizon-evidence.md` | Context rot, costs, cross-session gap |
| `research/04-prior-art-code-graphs-and-agent-memory.md` | Academic + OSS landscape, gap analysis |
| `research/05-cursor-integration-surfaces.md` | Hooks/MCP/CLI payload-level findings |
