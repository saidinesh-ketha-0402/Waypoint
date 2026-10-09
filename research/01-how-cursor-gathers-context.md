# How Cursor gathers and manages context (state as of Sep 2026)

## TL;DR

- **Retrieval is now grep-first, not embedding-first.** Cursor turned down its semantic/embeddings index (roughly mid-2026, Cursor 3.12+) in favour of a local "Instant Grep" index plus ordinary file reads. The agent decides what to search and read at runtime ("agent-as-retriever" / "just-in-time context").
- **Context exhaustion is handled by compaction.** The harness summarises the conversation when it nears the window; Cursor's own Composer model is RL-trained to *self-summarise*. Compaction is lossy by design.
- **Cross-session persistence is thin.** Cursor Memories are sentence-sized facts. Rules are static. Nothing natively carries *task state* (what was done, why, what failed, what's left) into the next conversation.
- **The harness is observable and injectable.** Cursor Hooks expose every tool call, edit, compaction and session boundary as JSON events, and `sessionStart` can inject `additional_context`. MCP servers add queryable tools. The CLI reports per-run token usage. These are the integration surfaces for anything we build.

---

## 1. Retrieval: how the agent finds code

### What changed
In 2026 Cursor removed the dedicated semantic-search path. From a Cursor staff reply on the forum:

> "Over the last months, models have gotten really good at using grep / indexed search, so the older dedicated semantic search path was rapidly no longer helping in a meaningful way. Agents now lean on our 'Instant Grep' tool (and normal file reads)... Cursor no longer needs to build a semantic index of your codebase which means we're no longer computing embeddings of your code or storing them on our servers for search."
> — [forum.cursor.com/.../165899](https://forum.cursor.com/t/what-do-you-think-about-cursor-removing-the-codebase-indexing-settings/165899); confirmed again in [.../167527](https://forum.cursor.com/t/codebase-indexing-not-working-in-v-3-12-onwards/167527/7)

Key facts about the current mechanism:
- The Instant Grep index is **per-repo, local only**, stored as binary files under `.git/cursor/`, anchored to git and layered with your uncommitted edits (no periodic rescan). Falls back to plain ripgrep if no index exists.
- `.gitignore` and `.cursorignore` are respected.
- The agent has `Grep`, `Read`, `Glob`, `Shell` and can spawn an **Explore subagent** that runs in an isolated context window so exploration noise doesn't bloat the parent.

### The older (still historically relevant) design
Cursor's earlier architecture is documented in [Securely indexing large codebases](https://cursor.com/blog/secure-codebase-indexing): Merkle-tree change detection → syntactic chunking → embeddings cached by chunk content → vector search; team members could bootstrap from a similar index via a "simhash". This is what most people (including the original idea statement) still assume Cursor does. It is no longer the primary path.

### Industry-wide pattern
Anthropic's [Effective context engineering for AI agents](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents) names the pattern **"just-in-time context"**: agents keep lightweight identifiers (paths, queries) and load data on demand via tools. Claude Code, Cursor, Windsurf, Cline, Amp all converged here by 2026 (survey: [Inside the agentic search stack replacing RAG in 2026](https://buzzgrewal.medium.com/ai-agents-dont-need-vector-search-anymore-inside-the-agentic-search-stack-replacing-rag-in-2026-58efcabe4f6f)).

**Implication for the idea:** "Cursor goes through files one by one with semantic search" is not accurate. It greps, reads targeted regions, and iterates. That iteration is exactly where tokens go: each grep→read→grep cycle re-derives structure (callers, dependencies, module boundaries) that a pre-built graph would answer in one call.

---

## 2. Context-window management: what happens when it fills up

### Compaction (harness-level)
From Cursor's [Training Composer for longer horizons](https://cursor.com/blog/self-summarization):

> "Many agent harnesses attempt to get around this by using compaction... When an agent hits its context limit, the harness transforms the context to a shorter length and continues... These approaches to compaction share the downside that they can cause the model to forget critical information from the context."

Two flavours are described: prompted summarisation (a summary model rewrites the history) and sliding window (drop old turns). A tuned prompt-based compaction produced ~5,000-token summaries.

### Self-summarisation (Composer-specific)
Composer 1.5/2 is trained with "compaction-in-the-loop": at a fixed token trigger the model pauses, summarises its own context (~1,000 tokens), and continues. Results: 50% less error from compaction vs. the tuned baseline, at one-fifth the tokens, with KV-cache reuse. Tested with 80k and 40k triggers. ([Composer 2 technical report](https://cursor.com/resources/Composer2.pdf))

The blog post shows a real self-summary from a 170-turn task. Its structure is instructive because it is essentially a hand-rolled **context graph in prose**: *User goal → What was implemented (per component) → Errors/fixes → What's still broken / to do for the next assistant → Paths.* That is the exact shape a resume brief should have.

### Observability
The `preCompact` hook exposes `context_usage_percent`, `context_tokens`, `context_window_size`, `messages_to_compact`, `is_first_compaction` — so a tool can *see* compaction coming and checkpoint before information is lost. ([Hooks docs](https://cursor.com/docs/hooks))

### Other token-efficiency levers Cursor uses
From [Improved token efficiency for longer agent runs](https://cursor.com/blog/improved-token-efficiency): dynamic loading of MCP tool definitions (−46.9% tokens in sessions using MCP), explicit prompt-cache breakpoints, delegating to subagents with fresh windows. Net 7% cost reduction without quality loss. These are harness-side; none of them persist knowledge across conversations.

**Implication for the idea:** The "last 25% gets dumber" intuition is directionally right but the mechanism is more nuanced (see `03-context-degradation...md`). Cursor already mitigates *within* a session. What it does not do is carry a faithful, queryable record of the work *across* sessions.

---

## 3. Cross-session persistence: what exists natively

| Mechanism | What it holds | Granularity | Team-shared | Task state? |
|---|---|---|---|---|
| **Rules** (`.cursor/rules/*.mdc`, `AGENTS.md`) | Static instructions/conventions | File/glob | Yes (in repo) | No |
| **Memories** | Auto-extracted or explicit one-sentence facts ("repo uses pnpm") | Per user × repo | No | No |
| **Skills** | Reusable procedures | Per tool | Yes | No |
| **Chat history / transcripts** | Full conversation text | Per conversation | No | Implicitly, but not queryable or injected |
| **Plans** (Plan mode) | A plan document | Per plan | Optional | Partially |

Sources: [Cursor Memories guide](https://localskills.sh/blog/cursor-memories-guide), [Cursor MCP memory overview](https://datamcp.app/blog/cursor-memory-mcp), [forum: handling context across tools](https://forum.cursor.com/t/how-are-people-handling-context-across-different-ai-coding-tools/159891).

A telling quote from the memory-server ecosystem: "the server does not guarantee that Cursor will call the memory tools at the right time. Encode the workflow in a focused Project Rule." In other words, **pull-based memory (agent must remember to call `memory_add`/`memory_search`) is unreliable**. This is the weakness the hook-based approach fixes.

---

## 4. Integration surfaces (the technical basis for an MVP)

See `05-cursor-integration-surfaces.md` for payload-level detail. Summary:

1. **Hooks** (`.cursor/hooks.json`, project- or user-level): 20+ lifecycle events. Every payload includes `conversation_id`, `generation_id`, `workspace_roots`, `transcript_path`. Notable events: `sessionStart` (can return `additional_context`), `beforeSubmitPrompt`, `postToolUse`, `afterFileEdit`, `afterShellExecution`, `afterAgentResponse`, `afterAgentThought`, `preCompact`, `stop`, `sessionEnd`.
2. **MCP servers** (`.cursor/mcp.json`): expose custom tools the agent can call; definitions are loaded dynamically to save tokens.
3. **Rules**: an `alwaysApply` rule can instruct the agent to call a resume tool first — the belt-and-braces fallback if `sessionStart` injection races.
4. **CLI**: `agent -p --force --output-format json "<prompt>"` runs headless and returns `usage: {inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens}` — the measurement instrument for a before/after benchmark. ([Headless CLI](https://cursor.com/docs/cli/headless.md), [Output format](https://cursor.com/docs/cli/reference/output-format.md), [forum confirmation](https://forum.cursor.com/t/cursor-agent-acp-never-reports-token-usage-over-acp-though-p-output-format-json-does/171923))
