# Guided equip capture: 2026-09-25 23:49

Evidence: `evidence/refresh/20260925-234927-5c2d0ebc964b4c358de5a217c324346a`

CW-36 (A) / FS-23 (B), PID 8364, registry revision 45 throughout.
The user confirmed all actions succeeded, including `equip_b`, whose note
was blank. Original evidence and session.json are unchanged.

## Results

Eight action phases recorded 25,211 live Unit observations (not unique
instances), zero native errors, and no world-list cap hits. Maximum recorded
individual native call time was 0.2221 ms, NOT total probe/frame overhead.
The `open_armory` phase's truncated flag was a metadata snapshot started at
the phase deadline; its native traversal reached all 11 world ordinals.
Other action phases were not flagged truncated.

Comparison uses cycle 1, grouping by world ordinal and Unit enumeration
ordinal, retaining the last observation when the cursor restarted. It is
non-atomic: ordinal equality is not proof of stable instance identity.
Partial cycle 2 is excluded from count comparisons. Opening the armory
temporarily changed the large world's count 2192 -> 2193 -> 2192 and
restarted its cursor; duplicates must not be counted as added instances.

### Armory-correlated smaller world

During the eight action phases, world ordinal 11 had:

| Phase | Units observed in cycle 1 |
| --- | ---: |
| idle | 20 |
| open_armory | 35 |
| preview_b | 35 |
| equip_b / equip_a / both repeats | 35 |
| close_armory | 20 |

The 15 additional resources comprise the same 12 custom model Unit IDs
(`5236bd49853b...`), two decoded resources (`4d1c334d294dfa97`,
`8a026ed4c1830deb`) and one unresolved tagged hash.
This is a strong armory-preview-world candidate, not proven local-player
ownership. In the earlier preflight, the 20-Unit world was at ordinal 9;
world indices must not be used as durable identifiers.

### Equip-correlated cluster in the large world

The large world (ordinal 1) had 2192 Unit ordinals in the reconstructed first
cycle in every action phase. Its resource multiset remained identical.
However, the ordinal-to-resource mapping changed on ALL FOUR confirmed equip
actions, within ordinals 2042..2055:

- preview_b -> equip_b: 13 ordinal/resource changes;
- equip_b -> equip_a: 13;
- equip_a -> equip_b_repeat: 13;
- equip_b_repeat -> equip_a_repeat: 13.

No corresponding ordinal/resource changes occurred for idle -> open_armory,
open_armory -> preview_b, or equip_a_repeat -> close_armory.
Ordinal 2045 retained its resource; the remaining 13 changed. The cluster
contains 12 custom-model resources, `8a026ed4c1830deb`, and the unresolved
tagged hash. Other custom resource groups at ordinals 2164..2175 and
2177..2188 did not reorder across these phases.

This repeated association makes the first cluster a useful equipped-model
candidate. It is NOT proof of destroy/recreate, a parent relationship, a
player handle, or a refresh function. Native array reordering is an
alternative explanation. These are observed ordinals, not memory offsets.

Both sampled armor choices expose the same 12 custom visual resource IDs.
Therefore an unchanged resource multiset does not imply equip did nothing,
and resource IDs alone cannot identify same-resource instance replacement.

## Probe limitations / next step

- All 612 initial Lua surface members remained unchanged. More broad static
  metadata sampling is unlikely to reveal the native equip controller.
- Each action occurs about 5.3 seconds after acknowledgment, but the
  interesting large-world cluster was usually reached around 11.6–14.9
  seconds (17.5+ on open). The smaller world was reached around 13.7–19.6
  seconds. We observed post-action states, not a precise event trace.
- Existing userdata tokens cannot establish native identity.
- Next probe should prioritize resource-matched candidate groups, fairly
  interleave worlds, and validate stable engine Unit identity read-only
  before comparing lifetime and attachment relationships. `Unit.id` and
  `Unit.scene_graph_parent` are exposed in the metadata, but their HD2
  contracts are NOT validated by this capture. Do not invoke them by guessing
  argument shape or infer player ownership from resource names.
- Validate candidate grouping automatically first. Then request a short
  targeted equip capture, preferably with distinguishable visual sources.
  Never silently recompile the user's 135 mappings merely for a control.
- A refresh implementation remains premature. No mutation, native hooks,
  deployment changes, game restart, or production protocol edits were made
  during this analysis.
