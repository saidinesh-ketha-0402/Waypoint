# Knowledge graphs vs. context graphs — fundamentals

## TL;DR

Your framing is correct and matches the 2026 literature:

- **Knowledge graph (KG):** *what exists and how is it linked.* Entities + typed relationships. Optimised for retrieval and structural queries. Largely static; a snapshot of "now".
- **Context graph (CG):** *why did this happen, under what rules, when, and based on what.* A KG extended with **time** (validity windows), **provenance** (which source/episode produced a fact), **situational scope** (which task/user/branch), and **reified decision traces** (a decision is itself a node you can attach reasoning, evidence and outcome to).

One-liner from TrustGraph: "A context graph *is* a knowledge graph. The distinction is not in the underlying data structure — it is in what the graph is optimized to do."

---

## 1. Knowledge graphs

**Model.** Nodes = entities (a function, a file, a person). Edges = typed relationships (`CALLS`, `IMPORTS`, `EXTENDS`). Optional properties on both. Two dominant formalisms: RDF triples (subject–predicate–object, queried with SPARQL) and labelled property graphs (Neo4j/Cypher, Memgraph, or embedded options like SQLite adjacency tables, KùzuDB).

**Strengths.** Multi-hop traversal ("who transitively calls X?"), aggregation ("hub" nodes by degree/PageRank), exact structural answers, cheap to store, deterministic to build when the source is code (AST parsing — no LLM needed).

**Weaknesses.** Static: says what is true *now*; overwriting a fact loses history. No notion of *why* an edge exists or who put it there. Conceptual/fuzzy queries ("where do we handle retries?") need a text/semantic layer on top.

**Code as a KG.** A code KG is the textbook case: tree-sitter parses 60–160 languages deterministically into functions/classes/imports/calls in seconds (Codebase-Memory indexes 49K nodes in ~6 s; the Linux kernel in ~3 min). Construction cost ≈ zero LLM tokens. The "initial cost is high" assumption in the idea statement does **not** apply to this layer.

---

## 2. Context graphs

Definitions converge across vendors and the one academic white paper:

- **contextgraph.tech:** "a living record of decision traces stitched across entities and time... captures the reasoning behind decisions — the exceptions, overrides, precedents." KG answers "what is X?"; CG answers "why was X decided?" ([source](https://www.contextgraph.tech/learn/context-graph-vs-knowledge-graph))
- **TrustGraph:** the defining mechanism is **reification** — turning an event or statement into a first-class node so metadata (model, user request, timestamp, reasoning chain) can hang off it. Two payoffs: **auditability** and **learning** (the system can re-ingest its own behaviour). ([source](https://trustgraph.ai/guides/key-concepts/context-graph-vs-knowledge-graph/))
- **Majmundar (Zenodo, Aug 2026):** formalises the CG as KG + four dimensions: **time, provenance, situational scope, decision traces.** ([source](https://exa.ai/library/publication/xly1v9xfs41))
- **Atlan:** "extends [the KG] at runtime with decision traces, temporal validity, and policy"; cites Gartner expecting >50% of enterprise agent systems to rely on context graphs by 2028; reports +38% SQL-generation accuracy when agents are grounded in governed context. ([source](https://atlan.com/know/context-graph-vs-knowledge-graph/))
- **Verdantix (skeptical view):** "incremental rather than transformational"; the hard part is that much decision context is never recorded digitally. ([source](https://www.verdantix.com/client-portal/blog/context-graphs--transformational-architecture-or-familiar-ai-hype))

**Why the Verdantix objection does *not* apply to coding agents:** in an agent harness, *every* decision is already digital — prompts, tool calls, edits, the agent's own explanations, test results — and Cursor's hooks emit all of it as JSON. A coding-agent context graph is the rare case where the capture problem is essentially solved by the platform.

### Comparison

| Dimension | Knowledge graph | Context graph |
|---|---|---|
| Core question | What exists? How linked? | Why did it change? Under what rules? When? Based on what? |
| Data | Entities + relations | + Events, decisions, constraints, attempts (reified) |
| Time | Snapshot | Bitemporal: `valid_at/invalid_at` (world) and `created_at/expired_at` (system) |
| Provenance | Usually absent | Every derived fact points to its source episode |
| Mutation | Overwrite | Invalidate, never delete (history preserved) |
| Optimised for | Retrieval / navigation | Agent decision-making, audit, precedent lookup |
| Standards | RDF/OWL/SPARQL, property graphs | Emerging; PROV-O for provenance; MCP as the delivery channel |

---

## 3. Temporal mechanics (how "why/when" is actually stored)

The most mature open implementation is **Graphiti** (Zep), ~20k stars, and it now literally describes itself as a framework for "temporal **context graphs**":

- **Episodes** are the raw inputs (a message, a document, a JSON record). Every entity and edge traces back to the episode(s) that produced it → provenance.
- **Bitemporal edges** carry four timestamps: `valid_at`, `invalid_at` (when true in the world) and `created_at`, `expired_at` (when the system learned/retired it). Contradictions **invalidate** old edges rather than deleting them, so "what did we believe on day 3?" is answerable.
- Incremental: new episodes integrate without batch recomputation.
- Results (Zep paper, arXiv 2501.13956): 94.8% on DMR vs MemGPT 93.4%; up to +18.5% on LongMemEval with 90% lower latency than full-context baselines.

Sources: [Graphiti README](https://github.com/getzep/graphiti), [Bi-temporal model docs](https://getzep-graphiti.mintlify.app/concepts/temporal-model), [Zep paper](https://arxiv.org/abs/2501.13956).

For an MVP, "bitemporal-lite" is enough: `created_at`, `superseded_by`, and a `session_id` on every decision node. That gives *why*, *when*, *by which session*, and *is it still current* without a full temporal query engine.

---

## 4. How the two layers map onto the coding-agent problem

| Layer | Nodes | Edges | Built from | Cost |
|---|---|---|---|---|
| **KG (structure)** | `File`, `Symbol` (function/class/method), `Module` | `CONTAINS`, `IMPORTS`, `CALLS`, `EXTENDS` | tree-sitter over the repo; incremental on `afterFileEdit` | Deterministic, no LLM, seconds |
| **CG (work trace)** | `Task`, `Session`, `Episode` (read/grep/edit/shell/response), `Decision`, `Constraint`, `Attempt` (failed), `OpenItem` | `Session PART_OF Task`, `Episode IN Session`, `Episode TOUCHED Symbol/File`, `Decision ABOUT Symbol`, `Decision DERIVED_FROM Episode`, `Decision SUPERSEDES Decision`, `Attempt FAILED_BECAUSE …` | Cursor hook payloads + transcript; light extraction of decisions from the agent's own prose | Near-free capture; optional cheap LLM for extraction |

The join between layers is the key design move: **a `Decision` or `Episode` points at KG nodes** (the symbol that was edited, the file that was read). That is what lets the next session ask "why does `PricingService.apply()` look like this?" and get both the structural neighbourhood *and* the reasoning from two sessions ago.

---

## 5. Related memory paradigms (for positioning)

Anthropic's taxonomy of context management ([cookbook](https://platform.claude.com/cookbook/tool-use-context-engineering-context-engineering-tools)):
1. **Compaction** — summarise the whole window when it grows (lossy, in-session).
2. **Tool-result clearing** — drop stale re-fetchable outputs (in-session).
3. **Structured note-taking / memory** — the agent writes notes to external storage and reads them back later (cross-session). Reported: memory + context editing → +39% on an agentic search eval; context editing alone −84% tokens on a 100-turn workflow.

A context graph is **structured note-taking done automatically and with a schema**: instead of the model deciding when to write a free-text note, the harness's telemetry writes typed, linked, provenance-bearing records — and a summariser reads them back as a resume brief.
