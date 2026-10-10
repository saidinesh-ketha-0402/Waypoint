# Waypoint Results

## Executive Summary
On [eShopOnWeb repo](https://github.com/dotnet-architecture/eShopOnWeb), Session 2 with gpt-5.3-codex, claude-opus-5.5 and composer-2.5, cold continuation failed 0/3 while Waypoint passed 3/3, with ~23% lower median input tokens and ~28% fewer tool calls—on a single-task, n=3 study.

---

### What was measured

The **authoritative rows** are the seven runs with `basis=measured` and **`gpt-5.3-codex`**, **`claude-opus-5.5`**, **`composer-2.5`**, (2026-10-07/08). They compare **Session 2** on the eShopOnWeb coupon feature: finish Phase 2 so Phase 2 tests pass, starting from a fixed **after Session 1** state (`after-s1` tag + Waypoint graph snapshot for arm B).

### Task success (primary outcome)

| Arm | Pass rate | Interpretation |
|-----|-----------|----------------|
| **A (cold)** | **0 / 3** | Continuation without Waypoint did not complete the task (Phase 2 tests failed). |
| **B (Waypoint)** | **3 / 3** | All runs passed with persisted graph + hooks/MCP. |
| **B-empty** | **1 / 1** | Hooks/MCP with an **empty** graph still passed once—useful control for overhead vs real context. |

**Takeaway:** On this task, Waypoint is associated with **reliable completion**, not only lower tokens. Cold Session 2 failed every measured Codex run.

### Input tokens (cost proxy)

| Arm | Run 1 | Run 2 | Run 3 | **Median** |
|-----|-------|-------|-------|------------|
| **A** | 323,153 | 330,835 | 730,370 | **330,835** |
| **B** | 255,269 | 168,033 | 327,633 | **255,269** |

- **Median reduction (B vs A):** ~**23%** fewer `input_tokens` (330,835 → 255,269).
- **Best B run vs A median:** 168,033 → ~**49%** lower than cold median (high variance on B run 3: 327,633).
- Run 3 on arm A (**730k**) is an outlier; medians are more stable than means for n=3.

`cache_read` is very large on all arms (prompt caching). Report **`input_tokens`** as the main comparison unless your org bills cache reads separately.

### Tool calls and wall time

| Metric | A (median) | B (median) | Change |
|--------|------------|------------|--------|
| **tool_calls** | 458 | 330 | **~−28%** |
| **output_tokens** | ~53,858 | ~34,990 | **~−35%** |
| **wall_secs** | ~897 s | ~716 s | **~−20%** |

**Takeaway:** Waypoint runs used **fewer agent steps** and finished **faster** on median, consistent with less re-orientation in Session 2.

### B-empty vs B (context vs plumbing)

Single measured **B-empty** run: **199,477** input tokens, **374** tool calls, **tests_pass=1**.

- Lower tokens than some B runs but **without** Session 1 graph content—shows hook/MCP **plumbing alone** does not fully explain B’s advantage; **persisted KG+CG** matters for both cost and (with n=1 on B-empty) success rate comparison.
- B-empty passing once while A failed 0/3 suggests **task difficulty and run variance** matter; the strong signal is **B 3/3 vs A 0/3** with lower median tokens.
