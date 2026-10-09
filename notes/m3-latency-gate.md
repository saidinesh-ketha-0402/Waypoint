# M3 Latency Gate Results

Date: 2026-10-05

## Setup

- Tool path: `~/.dotnet/tools/waypoint`
- Probe repo: `/tmp/wp-probe` (fresh `git init`)
- Input payload: `/tmp/ss.json` generated from `tests/fixtures/hooks/sessionStart.json` with `{{REPO}} -> /tmp/wp-probe`

## Commands

```bash
hyperfine --warmup 3 --runs 20 '~/.dotnet/tools/waypoint hook ping < /tmp/ss.json'
hyperfine --warmup 3 --runs 20 'DOTNET_TieredCompilation=0 ~/.dotnet/tools/waypoint hook ping < /tmp/ss.json'
sqlite3 /tmp/wp-probe/.git/waypoint/probe.db 'select count(*) from probe;'
```

## Results

- Default runtime:
  - Mean: `96.1 ms`
  - Min/Max: `90.8 ms` / `101.1 ms`
  - p95 (from exported JSON): `100.62 ms`
- `DOTNET_TieredCompilation=0`:
  - Mean: `99.9 ms`
  - Min/Max: `90.4 ms` / `111.6 ms`
  - p95 (from exported JSON): `106.95 ms`
- Probe rows inserted: `46` (20 runs + 3 warmups) x 2 benchmark commands

## Gate Decision

- M3 latency gate status: **PASS**
- p95 is below the `200 ms` threshold in both measured modes.
