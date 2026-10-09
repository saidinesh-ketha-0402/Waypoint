# Waypoint for .NET

> *Agents shouldn't have to re-survey the solution every session.*

**Waypoint** is a local **`dotnet tool`** that connects **Cursor** to two persisted graphs in **SQLite**:

- A **knowledge graph (KG)** of your C# / ASP.NET Core solution, built with **Roslyn** (types, members, DI registrations, HTTP endpoints, tests, and structural edges such as `IMPLEMENTS`, `INJECTS`, and `ROUTES_TO`).
- A **context graph (CG)** of agent work—reads, greps, edits, shell runs (`dotnet build` / `dotnet test`), and agent responses—captured automatically from **Cursor lifecycle hooks**, with decisions, failed attempts, constraints, and open items linked back to code symbols where possible.

On **session start**, Waypoint **pushes** a compact **resume brief** (token-budgeted) into the conversation. When the agent needs detail, **MCP tools** provide pull access: `resume`, `why`, `impact`, `what_changed`, `open_items`, `record_decision`, and `search`.

Nothing leaves your machine: data lives under `.git/waypoint/`; configuration under `.cursor/`.

---

## The problem

1. Real agent tasks on multi-project .NET solutions often span **multiple chat sessions** and context windows (`Web` → `ApplicationCore` → `Infrastructure` → tests → migrations).
2. **Compaction** and new sessions cause **amnesia**: what was done, what failed, which DI lifetime was tried, what remains.
3. The next session **re-greps and re-reads** the solution—and may **repeat dead ends** (for example scoped-in-singleton DI mistakes).
4. Structural questions (“who implements this interface?”, “which endpoint reaches this service?”) are expensive via exploration; a **semantic code graph** answers them in one query.

## How it works

```
 Session N                                        Session N+1
 ──────────                                       ────────────
 Cursor hooks ──► capture ──► ┌───────────┐       sessionStart ──► resume brief (≤ ~1.5k tok)
    read/grep/edit/shell/     │  SQLite   │                        goal · done · decisions ·
    response/preCompact/stop  │  KG + CG  │ ◄── MCP tools ──────── failed attempts · open items
                              └───────────┘      why / impact /     · hot files
 Roslyn ──► code graph ───────────┘              what_changed / open_items
```

| Mode | What happens |
|------|----------------|
| **Capture (push)** | Hooks write `Episode`s; edits map to `Symbol`s; compiler/test output and narration feed `Decision`, `Constraint`, `Attempt`, `OpenItem` nodes. Checkpoints run on `preCompact` and `stop`. |
| **Serve (pull)** | MCP tools answer structural and historical questions without re-exploring the repo. |
| **Re-inject (push)** | `sessionStart` adds a ranked brief with **unresolved constraints** and **ordered next steps**. |

---

## Technology stack

| Component | Technology |
|-----------|------------|
| CLI & packaging | C# / **.NET 10**, global **`dotnet tool`** (`waypoint`) |
| Code graph | **Roslyn** (`MSBuildWorkspace` + semantic model; fallback index modes available) |
| Context store | **SQLite** (`Microsoft.Data.Sqlite`) |
| Agent integration | **Cursor Hooks**, **MCP** (stdio, official C# Model Context Protocol SDK) |
| Visualization | `waypoint export` → `graph.json` + static HTML (Cytoscape) |

**MVP scope:** C# symbols are fully indexed; `.cshtml`, `.razor`, and config files are tracked as **file** nodes for the context graph. No cloud service or graph database is required.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` → `10.x`)
- [Cursor](https://cursor.com) with **Hooks** and **MCP** enabled
- A git repository containing your .NET solution (Waypoint stores state under `.git/waypoint/`)

Optional for debugging: `sqlite3` CLI (built in on macOS).

---

## Setup

### 1. Install the Waypoint tool

From this repository root:

```bash
./scripts/pack-install.sh
waypoint --help
```

This packs `Waypoint.Cli` and installs it as a global tool. Confirm `waypoint` is on your `PATH` (often `~/.dotnet/tools`).

### 2. Choose a target repository

Use any git-backed .NET repo, for example:

- **DemoShop** — sample under `tests/fixtures/DemoShop` (copy to its own folder if you want an isolated demo)
- **[eShopOnWeb](https://github.com/dotnet-architecture/eShopOnWeb)** or your team’s ASP.NET Core solution
- Your own repo, if a warm `dotnet build` / `dotnet test` completes in a reasonable time for demos

### 3. Copy Cursor templates into the target repo

```bash
cp -R templates/cursor/* /path/to/your/demo-repo/.cursor/
```

Templates include:

- `hooks.json` — wires Cursor events to `waypoint hook …`
- `mcp.json` — starts `waypoint serve` over stdio
- `rules/waypoint.mdc` — nudges the agent to call `resume` and record decisions when needed

Commit `.cursor/` in the demo repo and tag a baseline (for example `pre-task`) if you plan to benchmark.

### 4. Fix `waypoint` on PATH inside Cursor

If hooks fail because Cursor cannot find `waypoint` (common on macOS when Cursor is launched from the Dock), use the full path in `.cursor/hooks.json` and `.cursor/mcp.json`:

```text
~/.dotnet/tools/waypoint
```

### 5. Gitignore runtime noise in the demo repo

Add to the **demo repo’s** `.gitignore`:

```gitignore
.cursor/waypoint/
```

Waypoint’s database and logs live under **`.git/waypoint/`** and are not meant to be committed.

### 6. Build the code graph (one-time per repo / after large changes)

From the **demo repo** root:

```bash
waypoint index
```

For very large solutions, narrow scope:

```bash
waypoint index --project YourWebProject --project YourCoreProject
```

Note indexing time for your environment; subsequent runs skip unchanged files by content hash.

### 7. Restart Cursor and confirm integration

1. Restart Cursor after adding hook and MCP config.
2. In Cursor settings, verify **Hooks** and **MCP** show the Waypoint entries as loaded.

### 8. Smoke test

In a **new chat** in the demo repo:

1. Send `#task smoke test`.
2. Ask the agent to read `Program.cs`, add a comment to a known method (for example `PricingService.Apply`), and run `dotnet build`.

Then verify capture:

```bash
DB=.git/waypoint/graph.db
sqlite3 "$DB" "select id, title, status from task;"
sqlite3 "$DB" "select kind, summary from episode order by id;"
sqlite3 "$DB" "select n.qualified_name, t.how from episode_touch t join node n on n.id=t.node_id;"
tail -n 20 .git/waypoint/hooks.log
```

**Expected:** one active task named `smoke test`; episodes include prompt/read/edit/shell/response; touch rows show edited symbols; `hooks.log` has one line per event without failures.

**If nothing is written:**

1. Confirm `.git/waypoint/hooks.log` exists and read the errors.
2. Run `which waypoint` in Cursor’s integrated terminal.
3. Point hook/MCP commands at `~/.dotnet/tools/waypoint`.
4. Check Cursor’s hook output panel.

---

## CLI commands (reference)

| Command | Purpose |
|---------|---------|
| `waypoint index` | Build or refresh the Roslyn semantic index |
| `waypoint hook <event>` | Entry point for Cursor hook scripts (do not run manually in normal use) |
| `waypoint serve` | MCP stdio server for Cursor |
| `waypoint brief` | Print the resume brief (same content injected at session start) |
| `waypoint export` | Export task graph as `graph.json` and `graph.html` |

Global option: `--repo <path>` when not running from the repository root.

---

## MCP tools (reference)

When MCP is connected, the agent can call:

- **`resume`** — current task state and resume brief
- **`why`** — decisions and evidence for a symbol or file
- **`impact`** — implementations, injectors, and endpoints affected by a symbol
- **`what_changed`** — edits and touches for a task
- **`open_items`** — unresolved work and constraints
- **`record_decision`** — explicit high-quality decision records
- **`search`** — full-text search over the graph

---

## Repository layout

| Path | Description |
|------|-------------|
| `src/Waypoint.Core` | SQLite store, parsers, extractors, brief renderer, graph queries |
| `src/Waypoint.Roslyn` | Semantic indexer |
| `src/Waypoint.Cli` | `waypoint` executable and hook handlers |
| `src/Waypoint.Mcp` | MCP server |
| `src/Waypoint.Viz` | Graph export and HTML template |
| `templates/cursor/` | Cursor hooks, MCP, and rules to copy into target repos |
| `scripts/pack-install.sh` | Pack and install the global tool |
| `tests/` | Unit tests and DemoShop fixture |

More detail on product intent and benchmarks: see the ideation docs in the parent hackathon repo (`idea_csharp/README.md`). Step-by-step Cursor setup mirror: `templates/cursor/SETUP.md`.

---

## Privacy and security

- **Local only:** SQLite under `.git/waypoint/`; stdio MCP; no network dependency for core operation.
- **Reviewable:** Open-source-style .NET solution; hooks are explicit in committed `.cursor/hooks.json`.
- **IP:** Your source code is indexed and logged only on your machine as part of normal agent activity capture.

---

## License

See repository license file if present; otherwise treat as hackathon submission artifact for Trimble Global Hackathon 2026.
