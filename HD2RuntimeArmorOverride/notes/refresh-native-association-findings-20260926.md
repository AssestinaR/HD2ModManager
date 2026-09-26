# Native avatar-to-armor association — September 26, 2026

## Current conclusion

We now have direct native pointer-list evidence connecting the avatar
candidate to one complete 12-Unit custom armor assembly, excluding the
other 24 instances of the same resources in the sampled World tail.
This replaces proximity/resource-name inference with actual object edges.
It does **not** identify the refresh entry point or prove local-player
authority in multiplayer.

## Evidence (relative to evidence/refresh)

1. `20260926-102245-7cd197976d4145d59e67b0735a246795`
   - 39/39 freshly alive Unit handles resolved through the reviewed handle
     table, with index/generation and repeated-read validation.
   - All share method RVA `0x2beb90` at vtable slot `+0x1e8`.
   - `association-method.asm.txt` contains the offline disassembly.
2. `20260926-102534-4ec64d472f7d4000ada5475d051d450d`
   - 39/39 association reads complete; no native-query errors.
   - Avatar resource `4d1c334d294dfa97` has 18 direct links.
   - Exactly 12 links match custom armor resources `5236bd49853b*`.
   - 24 other instances with the same resources are absent.
   - Other six links were outside the resource allowlist; no identity claim.
   - Log is not truncated.
3. `20260926-102642-c2ef9bdc04f04fbd802af646efec43c3`
   - Same-process repeat independently reports the same 18/12/24 counts
     and pointers. All 39 candidate records are present.
   - Repetitive ordinary observations eventually reach the log byte cap;
     `truncated=true`. Do not treat the whole capture as complete.
     Removed redundant `unit_observation` emission from owner_native after
     this finding, retaining bounded candidates and batch/world summaries.
     This logging-only change is unit-tested, not yet recaptured in game.

Each association capture has `native-association-summary.json`, generated
by `scripts/summarize_refresh_associations.py`. The summarizer rejects
cross-phase pointer merging and duplicate-object lifetime ambiguity.

## Observed implementation

Reviewed method at executable RVA `0x2beb90` starts at `object+0x1d8`,
appends that pointer to an output array, then follows `child+0x1e0` until
NULL. The method can allocate; **we never call it**.

The read-only diagnostic instead follows that list directly, with:
- Exact executable fingerprint, method RVA and reviewed instruction gates.
- At most 64 edges; cycles/unreadable nodes rejected.
- Child vtable readability and same-World checks.
- Re-read of observed links, vtables, worlds, handle-table generation,
  object slot and manager references.
- No stale Unit handles retained across ticks; at most 48 candidates.

These are non-atomic observations. Rechecking reduces races but cannot
exclude ABA/recycling or provide an engine lock guarantee. A list edge
does not authorize dereferencing or calling arbitrary controller methods.

The executable fingerprint (1790149348 / 60719104) differs from game.dll
(1790161983 / 74727424) in runtime.status; do not mistake this module
difference for a detected game update.

## Next narrow investigation

1. Trace the verified link-list mutation/assembly path, starting from the
   already captured World.link_unit wrapper's reviewed direct call.
   Inspect code only; linking existing armor is not itself model refresh.
2. Identify the appearance controller that replaces this avatar's armor
   children, rather than rebuilding gameplay equipment or all World Units.
3. If a further manual transition is required, sample **this list and its
   owning controller** before/after one armory switch. Do not ask for more
   broad walking/resource captures.
4. Only after controller lifetime, arguments, execution thread and
   gameplay-independent scope are verified, consider a one-shot refresh
   experiment. No automatic native refresh is enabled now.

## Validation and environment

Lua suite passes, including actual helper tests for generation mismatch,
unsupported handles, unstable tables/links, cycles and zero native calls.
In-project Debug CLI build succeeds (five existing CA1416 warnings);
embedded-resource Core test passes. Deployments used CLI plan/validate/apply.
Probe stopped and the test game PID 66932 was terminated after capture.
Protocol revision 45 / 135 mappings remains untouched; SHA256:
`37BEE774415F1104312F039BB4290870221E0D1077D0E6FFFE7BBC994389A99C`.
No Git commit was requested or made.
