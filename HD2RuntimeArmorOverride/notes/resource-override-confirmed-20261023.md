# Runtime resource override confirmed / 2026-10-23

## 结论 / Result

`stingray.Application.set_resource_override` works on build 26088801 and gives
per-armour appearance redirection **without duplicating unit resources**.

Confirmed by observation, not by inference: the worn helmet unit was redirected
to another resident armour unit, the player opened the armoury, and the head was
immediately replaced by the donor piece. The player saw the head become a cape;
the resource index agreed.

Opening the armoury is sufficient. Switching to another armour set is **not**
required — the armoury open is what makes the game re-instantiate the armour
pieces and notify the renderer. Earlier attempts that changed nothing had simply
not triggered that refresh; the override had been registered but the already
rendered instances were never rebuilt.

## Exact call form

```lua
local Id64 = stingray.IdString64
local target = Id64.from_hex('af431352d9b95e83')  -- unit to be replaced
local donor  = Id64.from_hex('ea846ef460cc5ba0')  -- unit to load instead
stingray.Application.set_resource_override('unit', target, donor)
stingray.Application.clear_resource_override('unit', target)
```

Three arguments, in this order:

1. `type` — the resource type name string. `'unit'` is correct for units, and it
   is the same vocabulary `Application.can_get('unit', id)` accepts.
2. `name` — the resource to redirect, as an `IdString64`.
3. `override` — the resource to load instead, as an `IdString64`.

Both ids may be built with `IdString64.from_hex`; no resource *name* is needed.

## Evidence (probe 19, `tmp/probe/ovr4.lua`)

Counts are `World.units_by_resource(world, id)` for each hash, sampled inside one
tick so no handle goes stale.

| Moment | af43 (helmet) | ea84 (donor) | 3c33 | 45d7 | 91eb |
|---|---:|---:|---:|---:|---:|
| baseline, t=38 s | 1 | 2 | 1 | 1 | 0 |
| after `set_resource_override('unit', af43, af43)` (canary) | 1 | 2 | 1 | 1 | 0 |
| after `set_resource_override('unit', af43, ea84)` | 1 | 2 | 1 | 1 | 0 |
| t=56 s, armoury opened (helmet instance destroyed) | **0** | 2 | 1 | 1 | 0 |
| t=57 s, armoury open (helmet slot re-instantiated) | **0** | **3** | 1 | 1 | 0 |
| after `clear_resource_override('unit', af43)` | 0 | 3 | 1 | 1 | 0 |

`af43` never came back and `ea84` gained exactly one instance. That is the whole
mechanism: the game asked for the helmet, the engine handed it the donor.

The canary line matters on its own: a self-override (`af43` → `af43`) returned
without error, so the call form was already proven before any redirect was tried.

## Whole-armour run: 22 pieces, RE-824 shown as RE-2310

Probe 20 (`tmp/probe/wardrobe.lua`) registered the complete piece map from
`notes/re824-to-re2310-mapping.csv` — all 22 RE-824 unit ids redirected to their
RE-2310 counterparts — and the player reported that RE-824 now displays as
RE-2310. All 22 `set_resource_override` calls returned without error.

Two results in that run matter for how the manager should use the API.

**`can_get` is not a validity gate.** For every one of the 22 source ids,
`Application.can_get('unit', source)` returned **false**, while all 22 targets
returned true. The redirects applied and took effect regardless. So a piece whose
id this build does not report as available can still be redirected; gating the
apply step on `can_get` would have skipped the entire map and reported nothing
wrong.

**The armoury preview is not in `main_world()`.** `units_by_resource` on
`main_world()` returned 0 for all 44 ids for the whole run, even while the
armoury was open and the swap was visible on screen. The worn head and cape from
probe 19 did show up in `main_world()`, so piece counts are only meaningful if the
check iterates every world from `Application.worlds()` (8 to 11 of them on the
ship) instead of `main_world()`.

The override stays registered for the rest of the process and disappears when the
game exits; nothing is written to disk.

## What it does and does not do

- Redirects **future instantiation only**. The instance that was already worn was
  untouched until the game rebuilt it; `can_get('unit', af43)` stayed `true`
  throughout, so registration is not what changes.
- Needs a **refresh trigger**: opening the armoury makes the game destroy and
  recreate the armour pieces, which is when the redirect becomes visible. Without
  that refresh the registered override sits idle and nothing on screen changes.
- Is **reversible**: `clear_resource_override('unit', target)` returned without
  error and does not disturb instances that were created during the override.
- Costs **no extra VRAM**: one unit resource is loaded where another was
  requested. This is the property that per-armour duplicate copies could not give.
- Applies **per resource id**, so the mapping is naturally per armour piece.

## Corrections to earlier findings

Four earlier conclusions were wrong because of call-shape mistakes, and the
mistakes are worth recording so they are not repeated:

| Earlier belief | Reality |
|---|---|
| `set_resource_override(id, id)` crashes the engine | It has three parameters; omitting `type` put the target in the `type` slot. With `('unit', target, donor)` it returns cleanly. |
| Resource names must be recovered to use the override | Not needed. `IdString64.from_hex` is accepted everywhere a name is. `IdString64.to_string` cannot recover names anyway — it returns the `#ID[...]` form, so this build genuinely has no name table for these units. |
| `Unit.world_position(unit)` returns (0,0,0), positions unreadable | The signature is `world_position(self, i)` where `i` is the **node index**. With an index it returns real world coordinates: head node 11 was `(-0.067, 2.683, 21.686)`. Omitting it reads node 0, the root, whose pose is identity. |
| `World.link_unit(w, child, parent, node)` returns ok but does nothing | The signature is `link_unit(self, child, child_node_index, parent, parent_node_index)`. The earlier call passed a Unit where a node index belongs. |

Also corrected: `World.spawn_unit` does **not** instantiate a real unit resource
from Lua. Spawning a live unit's own resource id produced a degenerate unit
(`num_meshes` 1, `num_scene_graph_items` 1, a different `resource_name`), and
destroying it crashed the engine. Spawning a resource that is registered but not
resident (`can_get` true, no live instances) crashes immediately. Verification
must therefore go through the game's own instantiation path, which is what the
armour switch did.

## Unit identification so far

| Hash | Piece | Basis |
|---|---|---|
| `af431352d9b95e83` | head / helmet | hide test removed the head; 5 meshes, 114 scene graph items |
| `ea846ef460cc5ba0` | cape | rendered as a cape in the head slot when redirected; 4 meshes |
| `3c33cf10a26cbb3e` | cape | hide test removed the cape; 4 meshes |
| `45d79d84b8933a7f` | crotch | hide test removed the crotch; 5 meshes |

`ea84` and `3c33` both read as cape-like, so at least one of them is a secondary
cape piece rather than the main one. Identify by hide test before trusting the
table.

The post-switch histogram (`tmp/probe/n5hist.txt`) recorded 2293 units over 449
distinct resources, which is the raw material for a hash → piece catalogue.

## Next steps

Done on 2026-10-23: the per-piece catalogue exists in
`notes/re824-to-re2310-mapping.csv` (22 pairs, semantic labels), the manager
already parses each armour's slot/piece/body ids into `WardrobeSectionMapUnit`,
and the whole map has been applied and seen working in game.

Remaining:

1. Generate the pair table from the manager's wardrobe state instead of a
   hand-written CSV, so any armour can be the source and any armour the appearance.
2. Ship it as a boot payload: one `set_resource_override` per mapped piece, with
   no `can_get` gate (see above) and a `clear_resource_override` off switch.
3. Refresh: opening the armoury is enough. Confirm whether a mission load also
   rebuilds the pieces, so a mapping survives without a visit to the armoury.
4. Verify stability: repeated armoury visits, equipping an unmapped armour,
   entering and leaving a mission, and clearing the override mid-session.
5. Tell the user, in the UI, that the appearance updates when the armoury is
   opened — otherwise a registered map looks like it did nothing.

## Probe inventory

- `tmp/probe/ovr4.lua` — the confirmed single-pair run: canary, redirect, poll, histogram, clear.
- `tmp/probe/wardrobe.lua` — the confirmed 22-piece run; registers the whole map and watches it.
- `tmp/probe/run-probe.ps1` — deploy as `patch_7` with zero-byte companions, launch, click, screenshot.
- `tmp/probe/build_padded.py` — replace the boot payload in place, keeping archive layout.
- `tmp/probe/n5.txt` — probe 19 observation log (45k lines, poll output included).
- `tmp/probe/n6.txt` — probe 20 log: per-pair registration state, apply result, world counts.
- `tmp/probe/n5hist.txt` — resource histogram after the switch.
