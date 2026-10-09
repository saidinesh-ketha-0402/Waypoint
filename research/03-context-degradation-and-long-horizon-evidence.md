# Evidence: do agents really get worse as context fills, and what does re-orientation cost?

This note validates (and refines) two claims in the idea statement: (a) agents degrade as the context window fills, and (b) re-gathering context across sessions is a significant token cost.

## 1. Context degradation ("context rot") is real, gradual, and starts early

- **Chroma, *Context Rot* (18 models incl. GPT-4.1, Claude 4, Gemini 2.5):** "models do not use their context uniformly; their performance grows increasingly unreliable as input length grows," even on trivial tasks. Degradation is worse when the needle and question are semantically distant (i.e., realistic retrieval). ([source](https://www.trychroma.com/research/context-rot))
- ***Diagnosing and Mitigating Context Rot in Long-horizon Search* (arXiv 2606.29718, Jun 2026):** identifies **premature termination** — under long accumulated context, agents "give up or provide uncertain incorrect answers long before exhausting the context window." Rate is positively correlated with context length after controlling for difficulty. Context-management methods are shown to work by reducing premature termination. ([source](https://arxiv.org/html/2606.29718))
- ***Context Length Alone Hurts LLM Performance Despite Perfect Retrieval*:** 13.9–85% performance drop with input length across five models on math/QA/coding, **even when the relevant information is retrieved perfectly** and irrelevant tokens are replaced by whitespace. (Summarised in [AI Context Is Not Memory](https://andrewbaker.ninja/2026/08/16/ai-context-is-not-memory-why-agents-need-to-forget/).)
- ***Long-Horizon-Terminal-Bench* (arXiv 2607.08964):** long-horizon failures come from "accumulation of execution errors over extended trajectories"; agents time out after partial progress; the authors call for "stronger memory and progress tracking." ([source](https://arxiv.org/html/2607.08964v2))
- ***Polished but Unresolved* (arXiv 2609.00823):** late-stage agents drift toward "complete-looking submissions" with unresolved constraints. Two mitigations work: **constraint clarity** (an explicit list of what is still unresolved) and **action mapping** (an ordered next-step plan). ([source](https://arxiv.org/pdf/2609.00823.pdf))

**Refinement of the "last 25%" claim:** degradation is not a cliff at 75%; it is a slope that begins well before, and it manifests as scope drift and premature "done" rather than obvious errors. Cursor's `preCompact` hook reports `context_usage_percent`, so the effect can be observed and checkpointed against.

**Design consequence:** the last two papers tell us *what* a resume brief must contain to counter drift — an explicit *open constraints / unresolved items* list and an *ordered next steps* list. These map directly to `OpenItem` nodes in the context graph.

## 2. Compaction and summarisation are lossy

- Cursor: harness compaction "can cause the model to forget critical information." Even the RL-trained self-summariser is measured by *how much less error it introduces* (50% less than tuned prompt compaction), not zero. ([source](https://cursor.com/blog/self-summarization))
- Anthropic: compaction is "lossy by design"; "overly aggressive compaction can lose subtle but critical context whose importance only becomes apparent later." ([source](https://platform.claude.com/cookbook/tool-use-context-engineering-context-engineering-tools))

**Design consequence:** a summary should not be the *only* record. Keep the full typed trace in the graph and generate summaries *from* it; the agent can drill down when the summary is insufficient.

## 3. Re-orientation cost: exploration dominates token spend

- Codebase-Memory paper: "A typical codebase exploration session requires dozens of tool calls, consuming hundreds of thousands of tokens before the agent develops sufficient understanding to answer a structural question such as 'What breaks if I change this function?'"; "input tokens dominate the cost of agentic coding tasks, even with caching." ([arXiv 2603.27277](https://arxiv.org/html/2603.27277v1))
- Same paper, measured: graph queries answered structural questions at **~1,000 tokens vs ~10,000** for file exploration (10×), with 2.1× fewer tool calls, at 90% of the explorer's quality. Where the graph loses: queries needing full source context or exhaustive pattern matching → hybrid is optimal.
- Vendor claims (less rigorous, directionally consistent): code-graph-mcp reports 75–95% fewer tool calls per query type; codebase-memory's README claims ~3,400 vs ~412,000 tokens across five queries (120×) — treat as best-case marketing.
- LoCoBench-Agent (cited in the paper) shows a **comprehension-efficiency Pareto frontier**: thorough exploration and token efficiency trade off. Pre-materialised structure moves the frontier.

**Design consequence and honest expectation:** the biggest, most defensible saving is not "the agent never reads files again"; it is **skipping the re-discovery phase** in session N+1. A realistic target for the MVP benchmark is a 30–50% reduction in input tokens and tool calls for the *second* session of a two-session task, with equal or better task completion. Treat that as a hypothesis to be measured, not a promise.

## 4. Cross-session amnesia is a recognised, unsolved gap

- Every memory-MCP project in the ecosystem opens with the same problem statement ("Agents forget everything between sessions. They re-read files, re-discover architecture, and repeat mistakes." — GraphMem-MCP; "Sessions stop starting blind" — memex).
- The forum thread on cross-tool context concludes: "I haven't seen a setup I'd call production grade." ([source](https://forum.cursor.com/t/how-are-people-handling-context-across-different-ai-coding-tools/159891))
- Composer 2 report, limitations: "the model still faces challenges in extremely long-horizon tasks... Future work will likely focus on... longer-term memory architectures." ([source](https://www.wispaper.ai/en/blog/composer-2-technical-report-20260326/eng))

This is the problem you are solving. It is acknowledged by the platform vendor, by researchers, and by practitioners, and no shipping product closes it automatically for Cursor.
