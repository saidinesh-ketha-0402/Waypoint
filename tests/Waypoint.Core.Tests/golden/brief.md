## Waypoint resume brief — task: tighten pricing flow   (session 2 of this task · last active 0h ago · branch main)

**Goal.** tighten pricing flow
**Status.** in progress · 2 files in 2 projects changed · build: FAILED · tests: 2/3 FAIL (`dotnet test`) · migrations: 1 added, not applied

**Done so far (what & why).**
- `Shop.Core/src/Shop.Core/Services/PricingService.cs` — edited 4x — because no explicit decision captured yet
- `Shop.Core/PricingService.Apply` — edited 4x — because Switched to AddScoped for IPricingService. [s1]
- `Shop.Web/src/Shop.Web/Program.cs` — edited 2x — because no explicit decision captured yet

**Decisions & constraints still in force.**
- Switched to AddScoped for IPricingService.  [s1]
- TreatWarningsAsErrors must remain enabled for CI.  [s1]

**Tried and failed — do not retry.**
- PricingService.Apply -> Shop.UnitTests.PricingTests.Rounds_half_up failed.  [s1]
- PricingService.Apply -> System.InvalidOperationException: Cannot consume scoped service from singleton.  [s1]

**Open items / next steps (ordered).**
1. TODO: confirm MidpointRounding behavior for cart totals.  [s1]
2. Follow-up: add integration coverage around checkout failure path.  [s1]
3. Still need to run dotnet ef database update on staging.  [s1]

**Hot files.** `src/Shop.Core/Services/PricingService.cs` (edited 4x) · `src/Shop.Web/Program.cs` (edited 2x)

Drill down: waypoint.why(<symbol>), waypoint.impact(<symbol>), waypoint.what_changed(), waypoint.open_items()