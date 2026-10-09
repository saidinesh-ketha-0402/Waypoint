# Waypoint Cursor setup (M1)

This directory contains template files for `<demo-repo>/.cursor/`.

## 1) Install Waypoint tool

From the Waypoint repo:

```bash
./scripts/pack-install.sh
waypoint --help
```

## 2) Choose the demo repo

Use one of:
- DemoShop (copied from `tests/fixtures/DemoShop`, with its own git history)
- `dotnet-architecture/eShopOnWeb`
- your real repo, if warm build + tests complete in under one minute

## 3) Copy templates into the demo repo

```bash
cp -R <waypoint-repo>/waypoint/templates/cursor/* <demo-repo>/.cursor/
```

Commit the `.cursor/` files and tag `pre-task`.

## 4) Ensure PATH resolution inside Cursor

If Cursor cannot resolve `waypoint` (common on macOS when launched from Dock),
replace `waypoint` in `.cursor/hooks.json` and `.cursor/mcp.json` with:

```text
~/.dotnet/tools/waypoint
```

## 5) Cleanliness in demo repo

Add this line to `<demo-repo>/.gitignore`:

```gitignore
.cursor/waypoint/
```

Waypoint runtime artifacts are written under `.git/waypoint/` and are not committed.

## 6) One-time warm index

From the demo repo:

```bash
waypoint index
```

Record indexing time for the demo notes.

## 7) Restart Cursor and verify config load

Restart Cursor after adding hook/MCP config.
In Cursor settings, verify Hooks and MCP both show loaded entries.

## 8) M1 smoke check

In a fresh chat in the demo repo, send `#task smoke test`, then ask the agent to:
- read `Program.cs`
- add a comment to `PricingService.Apply`
- run `dotnet build`

Then run:

```bash
DB=.git/waypoint/graph.db
sqlite3 "$DB" "select id, title, status from task;"
sqlite3 "$DB" "select kind, summary from episode order by id;"
sqlite3 "$DB" "select n.qualified_name, t.how from episode_touch t join node n on n.id=t.node_id;"
tail -n 20 .git/waypoint/hooks.log
```

Expected:
- one active task named `smoke test`
- episode stream includes prompt/read/edit/shell/response kinds
- touch rows include `PricingService.Apply` with `edited`
- hooks log has one line per event and no failures

If no rows are written:
1. check `.git/waypoint/hooks.log` exists;
2. run `which waypoint` in Cursor integrated terminal;
3. switch command paths to `~/.dotnet/tools/waypoint`;
4. inspect Cursor hook output panel.
