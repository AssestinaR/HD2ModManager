# Entity-to-Unit ownership investigation

Probe v5 adds an explicitly requested `entities` mode, gated by same-session
World preflight. It calls World.entities(world), obtains that world's
EntityManager.unit_component(world), and queries UnitComponent.unit(manager,
entity) using only freshly enumerated Entity values. Returned Unit wrappers
must pass Unit.alive before their resource hash is read. No stale native
handles, mutation, spawn, destroy, or equip functions are used.

Original-engine contract references (not an HD2 ABI guarantee):
- https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/lua_ref/obj_stingray_UnitComponent.html
- https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/lua_ref/ns_stingray_EntityManager.html

## Actual runs

1. `evidence/refresh/20260926-002025-c575f3c0f3dd40b5b43eef0fd70d1363`:
   early startup, only eight worlds. Not a ship-scene conclusion.
2. `evidence/refresh/20260926-002202-820fc13b087a4fe0b97de2c01a55c614`:
   11 worlds, main world 2,192 Units / 228 Entity entries. Ten seconds covered
   only a subset of main-world Entity entries.
3. `evidence/refresh/20260926-002344-cbd7e988feab45968e0f3ad84fcc387a`:
   extended automatic 30-second phase to cover all 228 main-world Entity
   ordinals. Also covered the other worlds' 1, 3 and 2 Entity entries.
   577 Entity-to-Unit queries total; every returned userdata failed Unit.alive.
   Zero Lua query errors. No returned object was treated as a valid Unit.

The final run's maximum recorded native call was ~0.102 ms. This does not
measure total frame overhead. World list was not capped.

**Negative result:** this accessor did not connect enumerated Entities to the
known armor Unit groups. This does NOT prove that the game has no controller,
that Entities are unused, or that original-engine semantics are identical in
HD2. Calling a setter through these unvalidated references would be unjustified.

The initial guide printed "World/Unit preflight passed" even with zero live
Entity-linked Units. This misleading message was corrected: entity mode now
reports query completion separately from ownership validation.

## State and next direction

Lua suite and Debug builds passed. Probe embedded/deployed in the active
profile; production protocol revision 45 and SHA256 unchanged:
37bee774415f1104312f039bb4290870221e0d1077d0e6fffe7bbc994389a99c.
Sampling stopped; game PID 78800 remained responsive at the last check.
No gameplay-affecting experiment was performed.

Keep the 10-second user guide. Do not ask for more identical A/B captures.
Next useful work is a verified read-only cross-Unit link or native consumer
reference for the already observed changing model group, not blind calls
to World.update_unit or destroy/spawn as a substitute for the armor controller.
Experimental behavior calls must remain separate, opt-in and target-scoped.
