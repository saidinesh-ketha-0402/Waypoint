# Two-week plan → hackathon build order (.NET variant)

Assumption: the hackathon itself is a 24–48 h build. The two weeks before are for **de-risking, not building the product** (check the rules on pre-written code; keep spikes throwaway). Effort ~1–2 h per item, evenings/weekend. Items marked **(.NET)** are new or changed relative to `../idea/two-week-plan.md`.

## Week 1 — kill the unknowns

| Day | Spike | Done when | Fallback if it fails |
|---|---|---|---|
| 1 | **`sessionStart` injection race.** Minimal `hooks.json` with a `sessionStart` hook returning `additional_context: "WAYPOINT-MARKER-1234"`. Open 10 new chats in the C# repo; ask "what marker is in your context?". **(.NET)** Do this on the OS your team actually uses — if Windows, confirm the hook command resolves `waypoint` from PATH under Cursor's shell and that stdin/stdout JSON round-trips (no BOM, no CRLF issues). | ≥9/10 sessions see the marker; Cursor version and OS logged. | `postToolUse.additional_context` on first tool call; rule + MCP `resume`. Pick the most reliable as primary. |
| 2 | **Hook payload reality check on a .NET task.** Wire `postToolUse`, `afterFileEdit`, `afterAgentResponse`, `preCompact`, `stop` to a script that appends raw stdin to a log. Run a 20-minute real task that includes `dotnet build`, a failing `dotnet test`, and a compiler error. | You have the exact `tool_output` shape for Shell with `dotnet` output (is it truncated? where do `error CSxxxx` lines land?), `afterFileEdit` for `.cs` and `.csproj`, `transcript_path` presence, how `preCompact` fires. | If transcripts are disabled by policy, rely on `afterAgentResponse` + `afterAgentThought` only. If shell output is truncated, have the hook re-run `dotnet build -v q` itself? **No** — too slow; instead parse what is there and rely on the agent's prose. |
| 3 | **(.NET) Hook start-up latency + CLI token accounting.** (a) Build a hello-world `dotnet tool` that reads stdin JSON, opens a SQLite DB, writes one row, prints `{}`; publish ReadyToRun; time 20 invocations on your machine. (b) `agent -p --force --output-format json "say PONG"` and a small real task; confirm `usage` fields and that project hooks fire in CLI runs. | (a) p95 < 200 ms wall. (b) `input_tokens, cache_read_tokens, output_tokens` per run programmatically; hooks fire headlessly. | (a) If 200–400 ms: acceptable for the demo, note it. If > 400 ms: split into a Native-AOT hook binary (no Roslyn) + JIT `waypoint-index`; or use Python hooks from `../idea/` and keep only the indexer in .NET. (b) If hooks don't fire in CLI, run the benchmark from the IDE and read tokens from the `stop` hook / usage panel. |
| 4 | **(.NET) Roslyn indexer in ~150 lines.** `MSBuildLocator` + `MSBuildWorkspace.OpenSolutionAsync` on your repo → walk declarations, invocations, constructor params, `Add{Scoped,Singleton,Transient}<,>()`, `[Http*]` attributes → print counts and 10 sample `INJECTS`/`REGISTERED_AS`/`ROUTES_TO` edges. Time it cold and warm. Also time `CSharpSyntaxTree.ParseText` on your largest file. | Solution loads with ≤ a handful of diagnostics; < 60 s cold; ≥ 90 % of controllers' actions found as endpoints; DI registrations resolve to implementations; single-file syntax parse < 20 ms. | If `MSBuildWorkspace` fails or is slow: `CSharpCompilation.Create` over all `.cs` files with trusted-platform-assembly references (intra-repo symbols still resolve). If that fails: syntax-only KG (no `CALLS`). |
| 5 | **Pick the demo task on your ASP.NET repo.** Criteria: feature that genuinely needs 2 sessions — touches 5–8 files across ≥ 2 projects (e.g. service in Core, registration in `Program.cs`, controller/endpoint, EF migration, 2–3 tests), has a design choice (DI lifetime, rounding, where validation lives) and a plausible .NET dead end (scoped-in-singleton, banker's rounding, missing `[FromBody]`, migration drift). Write the Session-1 and Session-2 prompts; tag `pre-task`. Public fallback repo for a reproducible deck: `dotnet-architecture/eShopOnWeb` (Clean Architecture, xUnit, EF Core). | Prompts written; Session 1 run once manually; you know what a good Session 2 looks like and which test command proves completion (`dotnet test tests/UnitTests --no-restore`). | If your repo is too large/slow to build inside a 5-min demo, demo on eShopOnWeb and show the indexer output on your real repo as a second screen. |
| 6–7 | **(.NET) MCP server skeleton.** `ModelContextProtocol` SDK, stdio, one tool `resume` returning a hard-coded brief; register in `.cursor/mcp.json` as `{"command":"waypoint","args":["serve"]}`; add the rule; confirm the agent calls it in a fresh chat. Check tool discovery works on Windows (stdio, no console encoding surprises). | Tool appears and is called in a fresh chat on your OS. | — |

End of week 1 you know: the injection path that works, the exact payload shapes for `dotnet` output, your hook start-up cost, whether `MSBuildWorkspace` is viable on your repo, how to measure tokens, and the demo story.

## Week 2 — prepare the pitch and the harness

| Day | Item | Output |
|---|---|---|
| 8 | Write the **benchmark harness** as a throwaway script (`bench.ps1` if Windows, else `bench.sh`; see `demo-and-metrics.md`): reset repo (no `-x`), toggle Waypoint, run `agent -p`, parse usage, run `dotnet test`, append CSV. Warm `~/.nuget/packages` and `bin/obj` before the first run. | Harness working end-to-end with a dummy hook. |
| 9 | Run the **baseline arm** (no Waypoint) for Session 2, n=3. Note tokens, tool calls, time-to-first-edit, pass/fail, and whether the agent re-tried Session 1's dead end. | Baseline numbers ("cold session 2 costs ~X tokens, Y tool calls, re-tried the singleton registration in 2/3 runs"). |
| 10 | **Slides/one-pager** from `README.md` + `../findings/viability-analysis.md`. 6 slides: problem · evidence · insight (KG vs CG, Roslyn vs grep) · architecture · demo · Trimble relevance + roadmap. | Deck v1. |
| 11 | **Graph visual mock**: hand-craft `graph.json` for the demo task (nodes across Web/Core/Infrastructure/Tests, an endpoint diamond, a superseded decision) and render with cytoscape.js. | You know the visual works on a projector. |
| 12 | Dry-run the **demo script** with the mock; time it (≤ 5 min including one `dotnet test` run — pre-build so it takes seconds). Prepare the "why" and "impact" moments. | Demo script v1, timings. |
| 13 | Rehearse **judge Q&A** (`demo-and-metrics.md` §4), including the .NET-specific ones (Roslyn vs tree-sitter, hook latency, huge solutions, Razor). | — |
| 14 | Rest. Package spike learnings (payload samples, timings, workspace diagnostics) into a checklist for hackathon day. | — |

## Hackathon build order (priority-sorted; each line is a cut point)

```
H+0:00  Solution scaffold: Waypoint.Cli/Core/Roslyn/Mcp, System.CommandLine, SQLite schema, `waypoint index`
        (declarations + CONTAINS + INJECTS + REGISTERED_AS + ROUTES_TO from the day-4 spike)             [KG]
H+2:00  Hooks: sessionStart(brief stub) · beforeSubmitPrompt(task) · afterFileEdit (syntax-only refresh) [CG capture]
H+4:00  Hooks: postToolUse(read/grep/shell) with path(line,col)→span→node mapping;
        dotnet build/test output parser → structured shell episodes + attempt_failed staging             [CG capture]
H+6:00  Regex extractors on afterAgentResponse → claims with provenance                                  [CG extraction]
H+8:00  Brief renderer with ranking + token budget; wire into sessionStart/fallback                      [re-injection] ← demo-viable here
H+10:00 MCP: resume · why · what_changed · open_items · record_decision                                  [serve]
H+12:00 impact() through DI/endpoints (the C#-specific demo moment)                                      [serve]
H+14:00 preCompact/stop checkpoints over transcript_path; background `index` at stop                     [robustness]
H+16:00 Run benchmark arm B (Waypoint) n=3; compute deltas                                               [the number]
H+18:00 export + cytoscape visual (colour by project) for the demo task                                  [the picture]
H+20:00 CALLS edges + TESTS edges if semantic index is solid; search(); RESUME.md fallback; wording      [nice-to-have]
H+22:00 Freeze. Re-run demo path twice (pre-built, warm). Update deck with real numbers.
```

If two people: one owns `Waypoint.Roslyn` + MCP (`impact`, `why`) + visual, the other owns hooks + output parsers + extractors + brief + benchmark. Integration point is the SQLite schema — agree on it in the first 30 minutes and don't change it. If three: the third owns the benchmark harness and the deck from H+0.

## Definition of done for the MVP

- [ ] Fresh chat in the demo solution shows a Waypoint brief (via `sessionStart` or first-tool fallback) for the active task.
- [ ] `why <Type.Member>` returns at least one decision with a session tag and an evidence episode; at least one "tried and failed" item is derived from real `dotnet` output.
- [ ] `impact <IInterface>` lists the implementation, the injecting classes and at least one HTTP endpoint with verb + route.
- [ ] Benchmark CSV with n ≥ 3 per arm; deck shows tokens, tool calls, `dotnet test` success rate.
- [ ] Graph visual for the demo task renders, coloured by project, legible on a projector.
- [ ] Everything runs offline; `git status` in the demo repo shows only `.cursor/` config; hooks add < 200 ms per event (or the measured number is on the slide).
