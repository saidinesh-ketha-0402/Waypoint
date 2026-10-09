# Waypoint for .NET — a persistent context graph for multi-session coding agents on C# / ASP.NET Core repos

> *Agents shouldn't have to re-survey the solution every session.*

**One sentence.** Waypoint listens to Cursor's own lifecycle hooks, records what the agent did and *why* as a context graph linked to a **Roslyn-built code graph of the solution** (types, members, DI registrations, endpoints, EF entities), and hands the next session a compact resume brief plus query tools — so session N+1 starts where session N stopped instead of re-exploring the repo.

This folder is the C# / ASP.NET Core variant of `../idea/`. The problem, the insight, the pitch and the benchmark are the same; the *implementation* changes because the target repo is a .NET solution and the audience is a .NET shop. The differences are listed at the bottom.

---

## The problem (30 seconds)

1. Agentic coding tasks on real repos routinely outlive one context window — doubly so on a multi-project .NET solution, where a feature touches `Web` → `ApplicationCore` → `Infrastructure` → tests → an EF migration.
2. As the window fills, agents drift and terminate prematurely (Chroma *Context Rot*; arXiv 2606.29718, 2609.00823). Compaction helps but is lossy by the vendor's own admission.
3. A new session starts with **amnesia**: which projects matter, what was changed, which DI lifetime was tried and failed, what's left. The agent re-greps the solution — and now also has to rediscover its own edits.
4. Exploration dominates input-token cost; structural questions cost ~10× more via file exploration than via a graph (Codebase-Memory, 31 repos). In .NET the structural questions are *harder* for grep: "who implements `IPricingService`?", "which endpoint ends up calling this?", "is this registered scoped or singleton?" are all answered by Roslyn in milliseconds and by grep in a dozen tool calls.

## The insight

- A **knowledge graph** answers *what exists and how is it linked*. For C#, Roslyn gives this **exactly** (overload-aware, generics-aware, interface → implementation via the DI container registrations), deterministically, with zero LLM tokens.
- A **context graph** answers *why did this change, when, under what constraints, based on what evidence* — and every one of those signals is already emitted as JSON by Cursor Hooks. .NET adds unusually **structured** evidence: `error CS1061 …`, `Failed! - Failed: 2, Passed: 40`, `InvalidOperationException: Cannot consume scoped service … from singleton …`.
- Nobody wires the two together *automatically* for Cursor and *pushes* the result into the next session.

## What Waypoint does

```
 Session N                                        Session N+1
 ──────────                                       ────────────
 Cursor hooks ──► capture ──► ┌───────────┐       sessionStart ──► resume brief (≤1.5k tok)
    read/grep/edit/shell/     │  SQLite   │                        goal · done · decisions ·
    response/preCompact/stop  │  KG + CG  │ ◄── MCP tools ──────── failed attempts · open items ·
                              └───────────┘      why() / impact() /  hot files
 Roslyn ──► code graph ───────────┘              what_changed() / open_items()
 (solution · projects · types · members ·
  IMPLEMENTS · INJECTS · ROUTES · CALLS)
```

- **Capture (push):** hooks record every read, grep, edit, `dotnet build/test/ef` run and agent response as `Episode`s; edits are mapped to `Symbol`s in the code graph; agent narration and compiler/test output are mined for `Decision`, `Constraint`, `Attempt (failed)` and `OpenItem` nodes with provenance back to the episode. `preCompact` and `stop` trigger checkpoints before information is lost.
- **Serve (pull, when needed):** MCP tools `resume`, `why(symbol)`, `impact(symbol)` (through interfaces and DI to endpoints), `what_changed(task)`, `open_items`, `record_decision`, `search`.
- **Re-inject (push):** at `sessionStart`, a ranked, token-budgeted resume brief is added to the conversation's initial context, with an explicit **unresolved-constraints list** and an **ordered next-steps list**.

## What we will show

1. **Live demo:** Session 1 does half a feature on an ASP.NET Core repo (service + endpoint + migration + tests), hits a classic .NET dead end (e.g. registered a service as singleton that depends on a scoped `DbContext`), switches approach, and is closed mid-way. Session 2 opens, receives the brief, and finishes without re-exploring or repeating the dead end.
2. **The number:** same Session-2 prompt run headless via `agent -p --output-format json` with and without Waypoint. Input tokens, tool calls, time-to-first-edit, `dotnet test` pass/fail (n=3 each). Target: −30–50% input tokens on session 2 with equal success. Stated as hypothesis, measured live.
3. **The "why" and "impact" queries:** `why PricingService.Apply` → the decision, the session, the failed alternative, the exception that motivated it. `impact IPricingService` → implementations, injecting classes, and the HTTP endpoints that reach it — resolved through DI, which no grep can do.

## Why Trimble should care

- A large share of Trimble's services and desktop products are .NET; ASP.NET Core backends are the common denominator across business units. Multi-project solutions with decades of history make re-orientation the dominant cost of multi-session agent work.
- Token spend on agentic coding is a budget line; this is a measurable reduction with a local, zero-infra, no-code-leaves-the-machine footprint — installable as a `dotnet tool`, which every Trimble .NET developer already knows how to use.
- The pattern — *harness-emitted decision traces with provenance, replayed as context* — is what long-running agents on Trimble's Agentic AI platform / Agent Studio need, and doubles as an **audit trail**. A coding-agent MVP on a .NET repo is the cheapest proving ground, in the stack the platform teams use.

## Scope guardrails

- One .NET CLI (`waypoint`, a global `dotnet tool`), SQLite via `Microsoft.Data.Sqlite`, no graph DB server. Roslyn for the code graph. MCP via the official C# SDK.
- C# only in the MVP. `.cshtml` / `.razor` / `.json` / `.csproj` files are tracked as *file* nodes (the context-graph layer still works for them); they are not parsed for symbols.
- No LLM in the capture path except an optional single cheap call at checkpoints. Regex-first extraction with provenance so noise is inspectable.
- Everything lives in `.cursor/` and `.git/waypoint/`; nothing leaves the machine. Works on Windows and macOS/Linux (verify Windows hooks on day 1).

## What changes vs `../idea/` (Python/TypeScript version)

| Area | `../idea/` | This folder |
|---|---|---|
| Implementation language | Python 3.12 + `uv` | C# / .NET 10, shipped as a `dotnet tool` |
| Code-graph (KG) builder | tree-sitter (Python/TS grammars); weak call resolution | **Roslyn** (`MSBuildWorkspace` + semantic model); exact symbols, overloads, generics, `IMPLEMENTS`, `INJECTS`, `ROUTES` |
| Symbol identity | `qualified_name` string | Roslyn documentation-comment ID (`M:Ns.Type.Method(System.Int32)`) — stable, overload-aware; partial classes handled via a `span` table |
| ASP.NET-aware nodes | — | `project`, `endpoint` (route + verb), DI registrations, EF `DbContext`/entities/migrations, `test` methods |
| Failure evidence | shell exit code + regex on prose | **structured**: `error CSxxxx file(line,col)`, `dotnet test` summary, runtime exception type/message |
| Incremental KG refresh | per-edit tree-sitter re-parse | per-edit **syntax-only** Roslyn parse (fast); semantic edges refreshed in a background `index` at `stop` |
| MCP server | `mcp` Python SDK | `ModelContextProtocol` C# SDK (stdio) |
| Hook latency risk | negligible | **new day-1 spike** — .NET process start must stay < 200 ms (ReadyToRun, lazy-load Roslyn) |
| Demo dead end | generic | .NET-native: scoped-in-singleton DI error, banker's rounding (`Math.Round` default), EF migration mismatch |
| Non-goal | "not production accuracy for C#/C++" | C# accuracy is now a *strength*; C++ and Razor/Blazor component parsing remain non-goals |

Fallback if the all-.NET path costs too much time (see cut lines in `mvp-spec.md` and `two-week-plan.md`): keep the Python core from `../idea/` and add only a Roslyn sidecar (`waypoint-index`, .NET) that writes the KG into the same SQLite file.

## Files in this folder

- `mvp-spec.md` — architecture, data model, Roslyn indexer, hook handlers, MCP tools, resume-brief format, tech choices, cut lines.
- `two-week-plan.md` — pre-hackathon de-risking spikes (incl. the .NET-specific ones) and the hackathon build order.
- `demo-and-metrics.md` — benchmark protocol for a `dotnet test` repo, demo script with the DI dead end, pitch outline, anticipated judge questions.
