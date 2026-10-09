# Waypoint for .NET — MVP technical specification

Target: buildable by 1–3 people in a 24–48 h hackathon after ~2 weeks of part-time de-risking, on a **C# / ASP.NET Core solution**. Every component has a "cut line" so the demo survives if time runs out. Where this spec is silent, `../idea/mvp-spec.md` applies unchanged.

## 1. Architecture

```
<repo>/
  .cursor/
    hooks.json              → routes Cursor lifecycle events to `waypoint hook <event>`
    mcp.json                → registers `waypoint serve` (stdio MCP server)
    rules/waypoint.mdc      → alwaysApply backstop rule
  .git/waypoint/graph.db    → SQLite (inside .git, never committed)
  .git/waypoint/hooks.log

Waypoint solution (separate repo, shipped as a global dotnet tool `waypoint`)
  src/Waypoint.Cli/         System.CommandLine: index | hook <event> | serve | brief | export | bench
  src/Waypoint.Core/        SQLite store (KG + CG), ranking, brief renderer, extractors
  src/Waypoint.Roslyn/      solution loader + semantic walker → nodes/edges (loaded lazily; never on the hook path)
  src/Waypoint.Mcp/         7 tools over the same SQLite (ModelContextProtocol SDK)
  src/Waypoint.Viz/         export graph.json → static HTML (cytoscape.js)
  tests/                    xUnit
```

- One process per hook invocation (hooks are short-lived subprocesses reading stdin JSON). **The hook path must not touch Roslyn/MSBuild assemblies** — they are the slow part to load. Keep `Waypoint.Roslyn` behind an interface and load it only for `index`.
- SQLite in WAL mode handles concurrent hook + MCP + background-index access.
- Install for the demo: `dotnet tool install -g Waypoint.Cli` (or `dotnet tool install -g --add-source ./nupkg Waypoint.Cli` from a local pack). `waypoint` is then on PATH on Windows, macOS and Linux, so `hooks.json` has no OS-specific paths.

## 2. Data model (SQLite, ~9 tables)

### Knowledge-graph layer (structure)
```sql
node(id, kind, name, qualified_name, project, lang, meta_json, content_hash, updated_at)
  -- kind ∈ {solution, project, file, namespace, class, interface, record, struct, enum,
  --         method, constructor, property, field, endpoint, test, dbcontext, entity, migration}
  -- qualified_name = Roslyn documentation-comment ID, e.g. "M:Shop.Core.PricingService.Apply(System.Decimal)"
  --                  (stable across sessions, overload-aware). For files: repo-relative path.
  -- meta_json: endpoint → {"verb":"POST","route":"/api/basket/checkout"}; registration → {"lifetime":"Scoped"}
span(node_id, file_path, start_line, end_line)   -- one row per declaration site (partial classes have several)
edge(src, dst, kind, created_at)
  -- kind ∈ {CONTAINS, REFERENCES,   -- project → project
  --         CALLS, IMPLEMENTS, INHERITS, OVERRIDES,
  --         INJECTS,                -- class → interface/type taken in its constructor
  --         REGISTERED_AS,          -- interface → implementation (from services.Add*<I,T>() / AddDbContext / etc.)
  --         ROUTES_TO,              -- endpoint → action method / minimal-API handler
  --         TESTS}                  -- test method → symbol it exercises (via CALLS from a [Fact]/[Test]/[TestMethod])
```
Built by `waypoint index` (full, semantic) and refreshed per file on `afterFileEdit` (syntax-only; see §3). `INJECTS` + `REGISTERED_AS` are what make `impact()` resolve through DI: `IPricingService` → `PricingService` → consumed by `BasketService` → called from `BasketController.Checkout` → `POST /api/basket/checkout`.

### Context-graph layer (work trace) — unchanged from `../idea/`
```sql
task(id, title, goal_text, repo_root, branch, status, created_at, updated_at)
session(id /* = conversation_id */, task_id, model, started_at, ended_at, end_reason,
        input_tokens, output_tokens, cache_read_tokens)
episode(id, session_id, kind, ts, payload_json, summary)
  -- kind ∈ {prompt, read, grep, glob, shell, edit, response, thought, compact, stop}
  -- shell payload_json gains: {"dotnet":"build|test|run|ef|other","exit":1,"errors":[{"code":"CS0103","file":..,"line":..,"msg":..}],
  --                            "tests":{"passed":40,"failed":2,"failed_names":[..]},"exception":{"type":..,"message":..}}
episode_touch(episode_id, node_id, how)        -- how ∈ {read, matched, edited, ran, compiled, tested}
claim(id, task_id, session_id, kind, text, about_node_id NULL, status, confidence,
      derived_from_episode_id, superseded_by NULL, created_at, invalidated_at NULL)
  -- kind ∈ {decision, constraint, attempt_failed, open_item, fact}
```
"Bitemporal-lite" and provenance exactly as in `../idea/`. Full-text: `episode_fts`, `claim_fts` (FTS5 — included in `SQLitePCLRaw.bundle_e_sqlite3`).

## 3. Roslyn indexer (`waypoint index`)

### 3a. Full semantic index (run once; re-run in the background at `stop`)
1. Locate the solution: `--solution <path>` or auto-detect `*.sln` / `*.slnx` at the repo root (if several, pick the one named like the folder, else error with a list).
2. `MSBuildLocator.RegisterDefaults()` → `MSBuildWorkspace.Create()` → `OpenSolutionAsync`. Log `workspace.Diagnostics`; continue on partial failures (a project that fails to load becomes a `project` node with `meta_json.load_error`).
3. Per project: `GetCompilationAsync()`; walk every syntax tree with the `SemanticModel`:
   - Declarations → `node` + `span` (`ISymbol.GetDocumentationCommentId()` as `qualified_name`); `CONTAINS` edges; `INHERITS`/`IMPLEMENTS`/`OVERRIDES` from symbol info.
   - Invocations / object creations → `CALLS` (resolve via `GetSymbolInfo`; include candidate symbols on ambiguity with lower weight).
   - Constructors → `INJECTS` edges to each parameter type.
   - DI registrations: invocations of `Add{Scoped,Singleton,Transient}<TService,TImpl>()`, `Add*(typeof(..), typeof(..))`, `AddDbContext<T>`, `AddHostedService<T>`, `TryAdd*` → `REGISTERED_AS` with `meta_json.lifetime`.
   - Endpoints: methods on `ControllerBase` descendants with `[Http*]`/`[Route]` attributes → `endpoint` node (verb + composed route template) + `ROUTES_TO`; minimal API `app.Map{Get,Post,...}("route", handler)` → same, handler resolved to a method or a lambda node.
   - EF Core: classes deriving from `DbContext` → `dbcontext`; `DbSet<T>` properties → `entity` nodes; classes with `[Migration]` → `migration` nodes (`meta_json.id` = migration id).
   - Tests: methods with `[Fact]`, `[Theory]`, `[Test]`, `[TestCase]`, `[TestMethod]` → `test` nodes; `TESTS` edges = transitive `CALLS` depth ≤ 2 into non-test projects.
4. Write in one transaction per project; `content_hash` per file so unchanged files are skipped on re-index.

Performance target: full index of the demo solution < 60 s cold, < 15 s warm. If the real repo is a 100+ project solution, index with `--project <name>` filters and accept a partial KG — the CG layer works regardless.

### 3b. Incremental syntax-only refresh (on `afterFileEdit`, must be fast)
`CSharpSyntaxTree.ParseText(newText)` (no compilation, no MSBuild — milliseconds) → declarations, spans, attributes, `Add*<I,T>()` and `Map*()` patterns by *syntax*. Update `node`/`span`/`CONTAINS`/`REGISTERED_AS`/`ROUTES_TO` for that file; leave `CALLS`/`IMPLEMENTS` from the last semantic index in place (mark them `stale=1` for the file). Map edited line ranges → `span` → `episode_touch(how=edited)`.

A doc-comment ID needs the *semantic* symbol; in syntax-only mode, synthesise it from namespace + type + member name + parameter type *text* — good enough to match the semantic ID in >95 % of cases; reconcile on the next full index by `span` overlap.

### 3c. Cut lines, in order
1. Drop `TESTS` and EF nodes (keep DI + endpoints — they are the demo).
2. Drop minimal-API endpoint detection; controllers only.
3. Replace `MSBuildWorkspace` with a plain `CSharpCompilation.Create` over all `.cs` files under the repo, referencing the runtime's trusted platform assemblies (`AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")`). External (NuGet) types become error types; intra-repo symbols still resolve, which is all the graph needs. No MSBuild dependency, loads in seconds.
4. Syntax-only KG everywhere (no `CALLS`; `impact()` returns DI + CONTAINS neighbours only).
5. Last resort: keep the Python core from `../idea/` with `tree-sitter-c-sharp` for `CONTAINS` only.

## 4. Hook handlers (`waypoint hook <event>`)

All handlers: read stdin JSON, resolve `repo_root` from `workspace_roots[0]`, open DB, act, write stdout JSON (or `{}`), never throw (catch-all → log to `.git/waypoint/hooks.log`, exit 0). Hooks are fail-open; **budget < 200 ms** end-to-end including process start (see §10).

| Event | Handler logic (delta from `../idea/` in bold) |
|---|---|
| `sessionStart` | Upsert `session`. Find the active task for repo+branch → render brief → `{"additional_context": brief}`; set `env.WAYPOINT_TASK_ID`. Record whether injection happened. |
| `beforeSubmitPrompt` | Insert `episode(prompt)`; create task on first prompt; `new task:` / `#task` starts a fresh task. |
| `postToolUse` (matcher `Read\|Grep\|Glob\|Shell`) | Insert trimmed `episode`. Resolve paths and `path(line,col)` → `span` → `node` → `episode_touch`. **For `Shell`, classify `dotnet` commands and parse the output:** `error CS\d{4}` / `warning CS\d{4}` with `file(line,col)` → `errors[]` and `episode_touch(how=compiled)` on the enclosing symbol; `dotnet test` summary (`Passed!/Failed! - Failed: N, Passed: M` for VSTest; `Test run summary: Failed!/Passed! … failed: N, succeeded: M` for Microsoft.Testing.Platform) → `tests{}`; first `System.*Exception: message` line in output → `exception{}`; `dotnet ef migrations add` → `episode.kind=shell` with `dotnet=ef` and a `migration` node candidate. **A failing build/test/run right after an edit stages an `attempt_failed` claim keyed to the edited symbols, with the error code or exception message as the "why".** First `postToolUse` of a session: fallback brief injection if `sessionStart` didn't land. |
| `afterFileEdit` | Insert `episode(edit)`. **If `.cs`: syntax-only refresh (§3b).** Otherwise (`.cshtml`, `.razor`, `.csproj`, `appsettings*.json`, `.sql`): upsert a `file` node only and touch it. |
| `afterAgentResponse` | Insert `episode(response)`; run regex extractors (§5). Link `about_node_id` when a backticked identifier / `Type.Member` / path in the same sentence matches a node (match on `name`, then on doc-ID suffix). |
| `afterAgentThought` | Same, lower confidence. Cut line: skip. |
| `preCompact` | Checkpoint over `transcript_path` (extractors + dedupe); `episode(compact)` with `context_usage_percent`; optional LLM pass (§5b). Return `{"user_message": "Waypoint checkpoint: N decisions, M open items saved"}`. |
| `stop` | Checkpoint; update token counters; regenerate `.cursor/waypoint/RESUME.md`. **Spawn a detached `waypoint index --background` if any `.cs` file was edited this turn** so semantic edges are at most one turn stale. |
| `sessionEnd` | Close the session. |

## 5. Extraction (prose and tool output → claims)

### 5a. Regex / heuristic pass (always on, free)
Sentence-level classification as in `../idea/` (decision / constraint / attempt_failed / open_item), plus .NET-specific signals:
- **attempt_failed** from tool output (high confidence, 0.8): `error CS\d{4}`, `Build FAILED`, `Failed!`, `System\.\w+Exception: .+`, `Cannot consume scoped service`, `No service for type '.+' has been registered`, `Unable to resolve service for type`, `The migration '.+' has already been applied`, `Pending model changes`, `A second operation was started on this context`.
- **constraint** vocabulary: `must be (scoped|singleton|transient)`, `nullable`, `async all the way`, `ConfigureAwait`, `breaking change`, `public API`, `migration`, `connection string`, `appsettings`, `TreatWarningsAsErrors`.
- **decision** vocabulary (extra): `AddScoped|AddSingleton|AddTransient`, `instead of`, `switched to`, `MidpointRounding`, `IOptions`, `record vs class`.
- Symbol linking: `` `Foo.Bar` ``, `` `IFoo` ``, `Foo.Bar()`; also `file.cs(12,5)` locations from compiler output.
Confidence: 0.5 base; +0.2 if the sentence names a node; +0.2 if inside a "summary / what I did" block; +0.3 if derived from structured tool output; −0.2 if a question. Dedupe by normalised-text similarity > 0.85 within a task; `superseded_by` if the newer contains "instead" / "reverted" / "no longer" / "switched".

### 5b. LLM pass (optional, checkpoints only)
One call at `preCompact`/`stop` with the transcript tail (≤ 20k tokens) and a strict JSON schema (`decisions[]`, `constraints[]`, `failed_attempts[]`, `open_items[]`). Cheapest model. Cut line: regex-only — the `record_decision` tool + rule produce clean explicit records.

## 6. Resume brief (`waypoint brief`)

Hard budget **1,500 tokens** (`Microsoft.ML.Tokenizers` cl100k, or chars/4). Same sections as `../idea/`, with .NET-aware wording:

```
## Waypoint resume brief — task: <title>   (session 3 of this task · last active 2h ago · branch feature/discounts)
**Goal.** <goal_text, ≤60 words>
**Status.** in progress · 6 files in 3 projects changed · build: OK · tests: 38/40 PASS (`dotnet test tests/UnitTests`) · migrations: 1 added (AddDiscountPolicy), not applied

**Done so far (what & why).**
- `Shop.Core/PricingService.Apply(decimal)` — clamps discount to [0, 50%] — because <decision text>
- `Shop.Web/Program.cs` — `AddScoped<IPricingService, PricingService>()` — because PricingService depends on scoped DbContext   [s1]

**Decisions & constraints still in force.**
- Rounding uses `MidpointRounding.AwayFromZero`; tests assert 2 dp  [s1]
- Public API of `IOrderService` must not change (consumed by mobile client)  [s2]

**Tried and failed — do not retry.**
- `AddSingleton<IPricingService,…>` → runtime `InvalidOperationException: Cannot consume scoped service 'ShopContext' from singleton`  [s1]
- `Math.Round(x, 2)` default → `PricingTests.Rounds_half_up` fails (banker's rounding)  [s1]

**Open items / next steps (ordered).**
1. Wire `POST /api/basket/discount` in `BasketController` → call `IPricingService.Apply`
2. Fix `OrderTests.Applies_discount_on_checkout` (expects total 90.00, got 100.00)
3. Run `dotnet ef database update` locally; add migration to PR notes

**Hot files.** `PricingService.cs` (edited 4×) · `Program.cs` (edited 2×) · `BasketController.cs` (read 3×) · `PricingTests.cs` (edited 2×)

Drill down: waypoint.why(<symbol>), waypoint.impact(<symbol>), waypoint.what_changed(), waypoint.open_items()
```
Ranking identical to `../idea/`: `confidence × recency_decay × (2 if open_item) × (1.5 if about_node touched last session)`; claims derived from structured tool output get a further ×1.2.

## 7. MCP tools (`waypoint serve`)

`ModelContextProtocol` C# SDK, stdio transport, `[McpServerToolType]` class with seven `[McpServerTool]` methods. Compact Markdown responses, ≤ ~800 tokens each.

| Tool | Args | Returns |
|---|---|---|
| `resume` | `task_id?` | The brief. |
| `why` | `symbol_or_path` | Current + superseded claims about the node, evidence episode summaries, session tags. Accepts `Type.Member`, `IFoo`, a doc-ID, or a path. |
| `impact` | `symbol`, `depth=2`, `through_di=true` | Callers/callees; **implementations via `IMPLEMENTS`/`REGISTERED_AS`; consumers via `INJECTS`; reaching `endpoint`s with verb+route; tests via `TESTS`.** Nodes touched by the current task are flagged. |
| `what_changed` | `task_id?` | Edited symbols grouped by project, with per-symbol decision text and session tags; migrations added. |
| `open_items` | `task_id?`, `resolve_id?` | Ordered open items; resolve one. |
| `record_decision` | `text`, `about?`, `kind=decision\|constraint\|attempt_failed\|open_item` | Explicit claim, confidence 0.95. |
| `search` | `query`, `kind?` | FTS over episodes + claims, top 10 with provenance. |

## 8. Rule (`.cursor/rules/waypoint.mdc`)
```
---
description: Waypoint task continuity (.NET)
alwaysApply: true
---
This solution uses Waypoint for cross-session task memory.
- If no "Waypoint resume brief" is in your context, call `waypoint.resume` before exploring the solution.
- Before changing an interface, a DI registration, or an EF model, call `waypoint.impact` on it.
- When you choose a DI lifetime, add a migration, abandon an approach, or discover a constraint, call `waypoint.record_decision` (one sentence, name the type/member).
- Before declaring a task complete, call `waypoint.open_items` and resolve or list each item.
```

## 9. Visualisation (demo only)
`waypoint export --task <id>` → `graph.json` (touched symbols + claims + their DI/route neighbourhood). Static HTML with cytoscape.js; **colour by project** (Web / ApplicationCore / Infrastructure / Tests) and shape by kind (endpoint = diamond, interface = hexagon, claim = rounded box, superseded = dashed). Cut line: Graphviz `dot` PNG.

## 10. Benchmark harness (`waypoint bench` or `bench.ps1`/`bench.sh`)
Wraps `agent -p --force --output-format stream-json`; resets the repo to tag `after-s1` (`git checkout after-s1 -- . && git clean -fd` — **no `-x`, so `bin/`/`obj/` stay warm**); toggles Waypoint by renaming `.cursor/hooks.json`/`mcp.json`; runs `dotnet test <project> --no-restore` after each run for pass/fail; writes CSV `arm, run, input_tokens, cache_read, output_tokens, tool_calls, secs_to_first_edit, tests_pass`. Details in `demo-and-metrics.md`.

## 11. Tech choices
- **.NET 10 (LTS)**, single CLI packed with `<PackAsTool>true</PackAsTool>` / `<ToolCommandName>waypoint</ToolCommandName>`. `<PublishReadyToRun>true</PublishReadyToRun>`, `<TieredCompilation>` defaults, `<InvariantGlobalization>true</InvariantGlobalization>`, `<UseSystemResourceKeys>true</UseSystemResourceKeys>` to minimise start-up. Roslyn assemblies referenced from `Waypoint.Roslyn` only and loaded lazily.
- **Roslyn**: `Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`, `Microsoft.Build.Locator`. `SymbolFinder.FindImplementationsAsync` / `FindCallersAsync` for `impact` if the stored edges are stale.
- **SQLite**: `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` (FTS5 built in). WAL mode. Dapper optional.
- **MCP**: `ModelContextProtocol` (official C# SDK) + `Microsoft.Extensions.Hosting`.
- **CLI**: `System.CommandLine`. **JSON**: `System.Text.Json` source-generated contexts for the hook payloads (fast start, trim-safe).
- **Tokens**: `Microsoft.ML.Tokenizers` (Tiktoken cl100k) or chars/4.
- **Visual**: cytoscape.js via CDN.
- No Neo4j, no embeddings, no cloud, no daemon.

## 12. Non-goals (say them out loud)
- Not replacing Cursor's retrieval; augmenting it.
- Not parsing Razor/Blazor components, F#, VB.NET or C++/CLI — tracked as files, not symbols.
- Not a build system: Waypoint reads the solution; it never restores, builds or runs it (the agent does).
- Not multi-user sync (a git-tracked export is the obvious follow-up).
