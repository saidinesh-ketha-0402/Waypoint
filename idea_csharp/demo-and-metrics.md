# Demo script, benchmark protocol, pitch outline, judge Q&A (.NET variant)

## 1. Benchmark protocol (the number)

**Design.** One realistic two-session task on one ASP.NET Core solution. Session 1 is identical for both arms and is run once to create the "after-session-1" state (tag it `after-s1`; also snapshot `.git/waypoint/graph.db` → `graph.after-s1.db`). Session 2 is run from that state under two arms:

- **Arm A — cold:** `.cursor/hooks.json` and `.cursor/mcp.json` renamed away; rule removed. The agent gets only the Session-2 prompt.
- **Arm B — Waypoint:** hooks + MCP + rule active; `graph.db` restored from the snapshot before each run.

Same model, same prompt, n = 3 runs per arm (5 if time allows). Reset between runs: `git checkout after-s1 -- . && git clean -fd` — **without `-x`**, so `bin/`, `obj/` and the NuGet cache stay warm and build time is comparable across runs. Build once before the first run of each arm.

**Session-2 prompt template (both arms).**
> Continue the task "<title>". Finish the remaining work so that `dotnet test <tests project> --no-restore` passes. Do not re-implement what is already done.

(Arm A gets no more than this. If you feel the urge to add "the service is in `ApplicationCore`, it's registered in `Program.cs`" you are doing the agent's re-orientation by hand — that is the cost being measured.)

**Metrics per run** (from `agent -p --force --output-format stream-json`; final `result` event carries `usage`):

| Metric | Source | Why it matters |
|---|---|---|
| `input_tokens` (+ `cache_read_tokens` separately) | usage | The bill |
| `output_tokens` | usage | Secondary |
| Tool calls, split read/grep/edit/shell | count tool events in stream-json | Re-orientation effort |
| Seconds to first `afterFileEdit` | timestamps | How long until productive work |
| `dotnet build` / `dotnet test` invocations | count Shell events matching `dotnet (build\|test)` | Build/test churn — each one is slow and expensive in wall time |
| Wall time | harness | Latency (dominated by builds; report but don't lead with it) |
| Task success | `dotnet test … --no-restore` after the run | Savings mean nothing without this |
| Repeated failed attempt? | grep the transcript for Session 1's dead end (e.g. `AddSingleton<IPricingService`) | Quality signal: did arm A re-try what Session 1 already proved wrong? |

**Reporting.** Median and range per arm; percentage deltas; a one-line honesty note ("n=3, single task, single solution"). Expected: −30–50 % input tokens, −40–60 % tool calls, fewer build/test cycles, equal or better pass rate. If the delta is small, still report it — plus the qualitative wins (no repeated DI dead end, no scope drift into unrelated projects).

**Sanity controls.** Run arm B once with an *empty* graph to confirm the overhead of hook + MCP tool definitions alone (a few hundred tokens). Report hook latency from `hooks.log` (median ms per event) so nobody suspects the hooks slowed the agent down.

## 2. Live demo script (≤ 5 minutes)

Pre-flight: solution built; `dotnet test` warm; graph visual open on a second tab; `hooks.log` tailing in a side terminal.

0:00 — **Set the scene.** "Session 1 was yesterday. It implemented half of `<feature>` on `<solution>`: added `PricingService` in `ApplicationCore`, registered it as a singleton, got `InvalidOperationException: Cannot consume scoped service 'ShopContext' from singleton` at startup, switched to `AddScoped`, fixed a rounding test, and got interrupted before wiring the endpoint and the migration. I closed the chat." Show the graph: nodes coloured by project, the endpoint diamond still grey (not yet touched), the superseded singleton decision dashed.

0:45 — **Open a new chat.** Point at the Waypoint brief appearing in context (or on the first tool call). Read two lines aloud: the "Tried and failed — do not retry" singleton item with its `[s1]` tag, and open item #1 ("wire `POST /api/basket/discount`").

1:30 — **Give the Session-2 prompt.** While it runs, narrate what is *not* happening: no `Grep` storm across 40 projects, no re-reading `Program.cs` to find the registration, first edit lands in seconds and it's in `BasketController.cs`. Show `hooks.log` ticking: `afterFileEdit → BasketController.Discount (new endpoint POST /api/basket/discount)`, `postToolUse Shell → dotnet test: 40/40 PASS`.

3:00 — **The "why" and "impact" moment.** In the same chat: "Why is `PricingService` registered scoped, and what breaks if I change `IPricingService.Apply`'s signature?" → agent calls `waypoint.why` and `waypoint.impact` → answers: the decision and the exception that forced it `[s1]`; the implementation, `BasketService` and `OrderService` which inject it, the two endpoints that reach it with verb + route, and the three tests that cover it. Say: "Grep can find the string `IPricingService`. It cannot tell you that `POST /api/basket/checkout` depends on it through the container, or why the lifetime is what it is. The graph can."

3:45 — **The number.** Benchmark table: arm A vs arm B — tokens, tool calls, build/test cycles, pass rate, repeated-dead-end count. One sentence on method.

4:30 — **Close.** "Structure was free — Roslyn already knows your solution better than any grep. The trace was free — Cursor already emits it. We just stopped throwing it away. And it installs with `dotnet tool install`." Roadmap slide for 15 seconds.

## 3. Pitch outline (6 slides)

1. **Problem.** Multi-session tasks are the norm on multi-project .NET solutions; new session = amnesia; agents re-explore; tokens, builds and time burn; failed approaches (DI lifetimes, rounding, migrations) get retried. One quote from Cursor's compaction post about forgetting.
2. **Evidence.** Context rot (Chroma; arXiv 2606.29718); exploration dominates input tokens; 10× gap between graph and file exploration (Codebase-Memory, 31 repos); Composer 2 report lists long-term memory as future work. Add your own day-9 baseline: "cold session 2 on our repo: X tokens, Y tool calls, re-tried the singleton in 2/3 runs."
3. **Insight.** KG = *what/how linked* — for C# Roslyn gives it exactly, including DI and routes. CG = *why/when/under which constraints* — every signal is already emitted by the harness, and in .NET it's unusually structured (`CSxxxx`, test summaries, exception types). Nobody wires them back in automatically for Cursor.
4. **Waypoint.** Architecture: hooks → SQLite (Roslyn KG + CG) → brief at `sessionStart` + MCP tools. Push capture, push re-injection, pull drill-down. One `dotnet tool`.
5. **Demo + number.** Screenshot of the brief; the graph coloured by project; the A/B table.
6. **Why Trimble / next.** .NET is the common denominator across Trimble backends; token budget visibility; same pattern for Agent Studio agents with provenance as audit trail. Roadmap: Razor/Blazor components; team-shared graph via git export; C++ via LSP-backed KG for the native products; cross-tool (Claude Code, Codex) since everything is MCP + hooks.

## 4. Anticipated judge questions

**"How is this different from Codebase-Memory / code-graph-mcp?"**
Those build the *what* (structure) and rely on the agent pulling. Waypoint adds the *why/when* layer captured **from the harness automatically**, linked to structure, and **pushed** into the next session. For .NET specifically, our structure layer is Roslyn-semantic (DI, routes, overloads), where tree-sitter-based tools are name-matching.

**"Why Roslyn instead of tree-sitter?"**
Tree-sitter sees syntax; it can't tell which overload is called, which class implements `IPricingService`, or that `AddScoped<IPricingService, PricingService>()` wires them. Roslyn resolves all of that from the compilation the build already produces, deterministically, in seconds. The cost is solution loading, which we measured on day 4 (X s cold, Y s warm) and run in the background.

**"Hooks are .NET processes — doesn't that slow the agent down?"**
Measured: p95 ≈ N ms per event with ReadyToRun and lazy loading (Roslyn never loads on the hook path). Hooks are fail-open and asynchronous from the agent's point of view except `beforeSubmitPrompt`. If it mattered we'd Native-AOT the hook binary; it didn't.

**"`MSBuildWorkspace` is notoriously fragile on big solutions."**
Agreed. Three tiers: `MSBuildWorkspace` (exact); plain `CSharpCompilation` over all `.cs` files with framework references (intra-repo symbols still resolve, no MSBuild); syntax-only (no `CALLS`). Partial loads degrade gracefully — the context-graph layer keys on files and line ranges, so decisions, failed attempts and open items work even with zero semantic edges.

**"Our solution has 300 projects. Will it index?"**
Index with `--project` filters for the area the task touches; unchanged files are skipped by content hash on re-index; semantic refresh runs in the background at the end of a turn. A partial KG is fine; the brief does not depend on KG completeness.

**"What about Razor Pages / Blazor / `.cshtml`?"**
Tracked as file nodes — edits, reads and decisions about them are captured and appear in the brief and in `why`. Component-level symbols are a roadmap item (Razor compiler produces C# we could index).

**"Why not just use Cursor Memories or a rules file?"**
Memories are one-line facts, personal, not task state. Rules are static. Neither records the failed singleton registration, the in-flight migration, or evidence. Waypoint's brief is regenerated from a typed, provenance-bearing record every session.

**"Isn't this just a handoff document?"**
The brief *is* a handoff document — generated automatically, ranked, token-budgeted, and backed by a queryable graph so the agent can drill down (`impact` through DI, `why` with evidence) instead of trusting a lossy summary.

**"Does extraction hallucinate decisions?"**
Prose extraction can be noisy; that's why every claim links to its evidence episode and session and shows confidence. In .NET a large share of the valuable claims come from *structured* tool output (compiler errors, test summaries, exception messages), which is not noisy. The rule also nudges explicit `record_decision` calls.

**"Does it work for our Windows developers?"**
Yes — `waypoint` is a global `dotnet tool` on PATH, `hooks.json` has no shell-specific syntax, SQLite and MCP stdio are cross-platform. Verified on day 1 (say which OS you tested).

**"Security / IP?"**
Local SQLite inside `.git/`, stdio MCP, no network, no code leaves the machine. The tool is a reviewable .NET package; hooks are a committed `hooks.json`.

**"What's the business case?"**
Input tokens are the dominant cost of agentic coding; builds and test runs are the dominant wall-time cost on .NET. If a team runs multi-session tasks daily, a 30–50 % cut on every continuation session plus fewer build/test cycles is directly visible in the Cursor usage dashboard and in developer wait time. Plus fewer repeated dead ends.

**"How does this relate to Trimble's agent platform?"**
Same problem shape: long-running agents, lossy compaction, no record of why an agent acted. A harness-emitted context graph with provenance gives continuity *and* auditability. Building the proving ground in .NET means the platform teams can read, review and reuse the code.
