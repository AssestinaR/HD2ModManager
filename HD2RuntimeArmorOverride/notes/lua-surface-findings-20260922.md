# Armor Lua surface findings / 2026-09-22

## Result

The preferred next route is now Stingray's resource override API, not direct
mutation of an unidentified armor registry cache.

The game exposes these native Lua functions:

- `stingray.Application.set_resource_override`
- `stingray.Application.clear_resource_override`
- `stingray.Application.set_resource_override_flag`
- `stingray.Application.clear_resource_override_flag`
- `stingray.Application.can_get`
- `stingray.IdString64.from_hex`
- `stingray.IdString64.to_hex`
- `stingray.World.spawn_unit`
- `stingray.World.destroy_unit`
- `stingray.World.units_by_resource`
- `stingray.Unit.resource_name`
- `stingray.ResourcePackage.load`, `unload`, `flush`, `has_loaded`

Autodesk's Stingray Lua documentation describes `set_resource_override` as
redirecting future resource instantiation from one named resource to another.
It does not retroactively alter existing instances. This matches the desired
Unit reuse mechanism and explains why a normal equip/recreate operation is
the correct first refresh test.

## Exact File-ID validation

The Unit TypeID used by the manager is `0xe0a48d0be9a7453f`, equal to the
resource hash for the Stingray type name `unit`.

The read-only probe converted known RE-2310 Unit FileID
`17da843a4908511e` with `IdString64.from_hex`, round-tripped it through
`IdString64.to_hex`, and passed the exact ID object to:

```lua
Application.can_get('unit', id)
```

The call succeeded. Availability timeline from a cold game launch:

| Probe stage | Elapsed | Available |
|---|---:|---|
| startup | approximately 0 s | false |
| sample 1 | approximately 0 s | false |
| sample 2 | approximately 5 s | false |
| sample 3 | approximately 15 s | true |
| sample 4 | approximately 30 s | true |
| sample 5 | approximately 60 s | true |
| sample 6 | approximately 120 s | true |

This establishes that:

1. exact hashed Unit IDs are accepted by the Lua resource API;
2. the loader executes before this armor Unit becomes available;
3. resource registration occurs between the 5 and 15 second samples in this
   run;
4. an override can potentially be registered early, or conservatively after
   both source and target Units become available.

It does not yet establish the accepted argument types or source/target
direction of `set_resource_override` in this HD2 build. Do not guess these in
a multi-Unit experiment.

> Answered on 2026-10-23: see `resource-override-confirmed-20261023.md`. The call
> is `set_resource_override('unit', target_id, donor_id)` with `IdString64`
> arguments from `IdString64.from_hex`; no resource names are needed, the redirect
> takes effect the next time the game instantiates the resource (opening the
> armoury is enough), and `clear_resource_override('unit', target_id)` reverts it.

## Lua visibility boundary

Only a small set of ordinary Lua modules was loaded during the two-minute
startup sample: boot, Wwise callbacks, a reflection-probe module, standard
libraries, the loader and this probe. No semantic armor, inventory, loadout
or customization Lua controller became visible.

The engine native surface is rich enough to query resources and operate on
Unit instances, but the game's armor selection/controller layer is not
currently visible through `package.loaded` or top-level globals. The resource
override API is therefore the stronger path; native registry-cache discovery
can remain a fallback.

## Probe behavior

Source:

- `HD2RuntimeArmorOverride/src/lua_surface_probe.lua`
- `HD2RuntimeArmorOverride/tests/test_lua_surface_probe.lua`

Captured log:

- `HD2RuntimeArmorOverride/evidence/lua-surface-sample-20260922.jsonl`

The probe:

- performs no FFI or process-memory operation;
- does not require unknown modules;
- enumerates raw table metadata without triggering metatable getters;
- limits work per update tick;
- records function identity and names, not game/account scalar state;
- makes only allowlisted `IdString64` conversion and `Application.can_get`
  calls;
- restores its own update wrapper after 120 seconds;
- completed with no probe error or truncation.

The local LuaJIT fixture validates duplicate initialization, callback
forwarding and restoration, nil return preservation, JSON escaping, log
failure, original callback errors and the allowlisted ID query.

## Next controlled test

Build a separate, explicitly enabled experiment using one compatible target
Unit A and donor Unit B:

1. Convert both known FileIDs through `IdString64.from_hex`.
2. Wait until `Application.can_get('unit', A)` and
   `Application.can_get('unit', B)` are true.
3. Call one resource override only, with a reversible log and
   `clear_resource_override` cleanup path.
4. Let the user equip or preview the target armor so the game creates a new
   Unit instance through its ordinary path.
5. Verify the target piece shows B, donor and control armor remain correct,
   and switching repeatedly does not crash or retain the wrong visual.

The first write experiment must use two Units with matching role/body variant
and compatible skeleton/material expectations. It should not redirect an
entire armor set yet. It also must not claim memory or GPU savings until a
later package removes the redundant donor payload and measurements confirm
reduced residency.

## Current environment

The active test profile contains only Bingus Shared Loader and HD2 Armor Lua
Surface Probe. The old wide memory-scanning probe was removed from the active
profile. The latest surface probe package is deployed. The game was stopped
after sampling.
