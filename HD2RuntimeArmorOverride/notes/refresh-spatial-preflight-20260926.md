# Spatial preflight, September 26, 2026

Evidence: `evidence/refresh/20260926-091154-2573675f43bf4898a210fd465f87dd2d`.
Game PID 9408, ship scene, runtime revision 45. Production protocol SHA256
`37bee774415f1104312f039bb4290870221e0d1077d0e6fffe7bbc994389a99c`
before and after sampling. No equipment actions or runtime writes.

An explicit `spatial` mode reads `Unit.world_position(unit, 1)` and the
Vector3 coordinates for the bounded tail of the first enumerated World.
It first validates World access in the same session, reacquires Unit handles
per tick, checks `Unit.alive`, and stops on any unexpected native result.
The original Stingray API uses node 1 for root-position examples; this run
confirms only that this call succeeded for these HD2 Units, not the meaning
of every coordinate or the existence of a cross-Unit link.

The 20-second phase made 3,283 live Unit observations, 1,054 with positions,
with zero Lua/native-call errors and no log truncation. Observations repeat
across non-atomic traversals; they are not unique instance counts.

In the first complete spatial pass the 12 custom-model resource IDs occur
in three groups at distinct root positions:

| World-1 ordinal window | Root position (x, y, z) | Neighboring same-position resources |
| --- | --- | --- |
| 2108-2119 | (0.00, 2.51, 20.01) | 2105 `40ae90fff236865b`, 2106 `d40f23a128bd988d`, 2107 `8a026ed4c1830deb`, 2120 unresolved |
| 2164-2175 | (1.47, 13.87, 19.57) | 2176 unresolved |
| 2177-2188 | (-3.20, -1.28, 20.31) | 2189 unresolved, 2190 `9443b6dc83b9e4bb` |

The earlier equip-correlated group occurred near ordinals 2042-2055.
Its ordinal moved during scene loading; ordinals cannot be baked into a
locator. The first group has multiple colocated non-custom Units and is
consistent with the equipped-model candidate, but static position and array
adjacency do not establish ownership. The other two groups are separate
spatial instances using the same resources, not duplicate observations at
one location.

Next useful validation is a short *movement-correlated* capture: move the
ship character without entering the armory, and verify that exactly one
custom-resource group tracks the character along with its nearby non-custom
root Units. That identifies the live-character candidate independently of
the equip transition. It still would not identify the native refresh
controller. Do not call a guessed update, spawn, or destroy function based
solely on this evidence.

After starting the game and entering the ship, run
`scripts/Watch-ArmorRefresh.ps1 -SpatialMovement`. The guide performs
same-session World/spatial preflights, then cues only two ten-second phases:
stand still; walk a few steps and stop. It never changes equipment. Send
the resulting evidence directory for correlation. This is not another
eight-stage armor A/B test.

## Movement capture: 09:20 session

Evidence: `evidence/refresh/20260926-092027-0185bd8c34444ffdb64d8bcacb6d6f99`.
The user confirmed both phases succeeded. PID 81952, revision 45, 11
Worlds, no native errors or phase truncation. The preflight made 3,283
live Unit observations; the `still` and `walk` phases made 1,948 and
1,994 respectively. These are repeated observations.

Preflight at 2.13 and 7.34 seconds observed the first custom-resource group
move from approximately `(-0.90, 0.16, 20.01)` to
`(-1.05, 0.24, 20.01)`. All 12 custom Units plus adjacent resource IDs
`40ae90fff236865b`, `d40f23a128bd988d`, and `8a026ed4c1830deb`
moved together. This supports a shared moving assembly, not a proven
native parent/owner. The two other same-resource groups retained their
distinct positions when observed.

In the explicit `walk` phase, the first group was only reached at 1.68
seconds, before the cue at roughly 3.3 seconds. The scan then visited the
other groups at 3.40 and 3.56 seconds and did not revisit the first group
before the 10-second deadline. The unchanged first-group position in this
phase is therefore **not** a negative movement result. The prior probe
rotated among all 11 Worlds, producing insufficient temporal coverage.

The subsequent diagnostic-only `spatial` mode selects the current largest
World by fresh Unit count and repeatedly scans its tail window; it does not
assume world ordinal 1 persists, and does not mutate instances. A future
short guided walk capture can test whether that first group tracks the
actual player during the action window. Even a positive result would still
not locate the equip refresh controller.

An unattended 09:27/09:28 check started the game but observed only eight
Worlds and a largest World of one Unit. This was an early/menu scene, not
the ship, so it cannot validate tracking cadence for the player group.
The guide now requires a largest World of at least 256 Units for its
spatial preflight and reports an explicit ship-scene readiness failure
otherwise; it does not count repeated observations of the single menu
Unit as meaningful coverage.
