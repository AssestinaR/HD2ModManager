# Avatar owner investigation — September 26, 2026

## Scope and current result

The movement capture established a strong worn-assembly candidate and a
persistent avatar candidate. This turn tests whether the avatar exposes
equipment/controller references through Lua dynamic data, and examines its
original resource dependencies. Neither route has yet produced a refresh
function or a verified runtime owner-to-armor edge.

No equipment mutation, native hook, debugger attachment, anti-cheat change,
manual Unit spawn/destroy, or guessed refresh call was performed.

## Native accessor contracts

Original-engine documentation (not an HD2 ABI guarantee):

- https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/lua_ref/obj_stingray_Unit.html
- https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/lua_ref/obj_stingray_UnitComponent.html

`Unit.scene_graph_link(unit, node, parent_node)` modifies nodes within one
Unit; it is not an ownership query. `scene_graph_parent` returns a node index,
not an owner Unit. `is_child` exists in HD2's observed API table, but its
signature was not found in the consulted original-engine reference or local
example projects. It was not invoked.

`Unit.has_data(unit, key)` checks a script-data key, and `get_data` reads a
value when present. This is not a generic view of native object fields.
The documented accessor requires indices; a zero-key call to dump everything
was not assumed valid. The previous Entity path's `UnitComponent.unit(manager,
entity)` signature does match the original documentation; it must not be
"fixed" by substituting an unverified integer component handle.

## Probe v6 and measured negative result

Added opt-in `owner_data` mode, gated by a same-session World preflight.
It selects the largest currently enumerated World and searches the bounded
tail for resource `4d1c334d294dfa97`. Each candidate gets ten hypothesized
key checks, one per encounter/batch, with an eight-candidate session limit.
Handles are reacquired each tick; only identity labels/progress are retained.
Unexpected result types stop native queries. Data values are not logged;
table inspection permits only bounded raw traversal and allowlisted key
names/types, with no metatable access or returned-function calls.

Evidence:
`evidence/refresh/20260926-095632-bf738725681a40dbad2ec16a19435aa5`

- Automatic Steam launch, PID 38152.
- Eleven Worlds, largest observed World 2,206 Units.
- 6,644 live Unit observations, not unique instances.
- One avatar candidate (ordinal 2080 in this session, NOT a persistent index).
- Ten successful `has_data` calls; all returned false:
  `extensions`, `extension`, `owner`, `player`, `player_unit`, `inventory`,
  `equipment`, `customization`, `armor`, `character`.
- No `get_data` call was made; no key passed the presence check.
- Zero native/Lua query errors; max individual recorded call 0.3093 ms.
- All ten checks completed by 3.706 seconds. Phase end at 10.005 seconds
  reports `truncated=true` because a separate metadata snapshot was pending.
  Do not describe the entire capture as untruncated; native summary and all
  requested candidate-key results are present.

This excludes only these ten script-data keys on this observed avatar.
It does not exclude a native controller, different keys, another Lua state,
an external component manager or other unobserved instances. More arbitrary
key guessing is not the recommended next step.

## Static asset evidence

The manager CLI's `gamedata asset find` found the avatar Unit in archive
`18235e0c9ec0e636` (Super Destroyer Interior 2), with a current trusted
original-game index. Existing `CanonicalAvatarRigReader` independently
uses the same archive and FileID.

Used the existing read-only `HD2ModAdaptation` package resolver to extract
bounded resource payloads into `evidence/refresh/avatar-static/`.
`manifest.json` records exact TypeID/FileID pairs, lengths, offsets and
SHA256; the game files were not modified. FileID alone is not sufficient:
the archive has bones, ragdoll, physics, state-machine and Unit resources
sharing the avatar FileID.

Avatar Unit:
- FileID `4d1c334d294dfa97`, TypeID `e0a48d0be9a7453f`, 21,504 bytes.
- SDK header layout identifies a composite reference `c6a69260cc389bcd`.
- Composite TypeID `c4f0f4be7fb0c8d6`, payload 14,896 bytes.
- Its bounds-checked Unit table contains 13 entries. The SDK parser calls
  these Unit/type references plus per-Unit mesh/stream metadata.
- Entries include the avatar itself and two independently observed moving
  objects: `1c8764d62e12925a` and `a9936cbe561e8180` (local name-table
  suffixes indicate PDA and sample pouch). The exact table is saved as
  `composite-summary.json`.
- None of the test custom-model IDs (`5236bd49853b...`) occur in that table.

This is a shared composite mesh-resource definition, NOT proof of a runtime
parent-child graph or an armor-refresh controller. Its explanatory value is
that some stable, co-moving objects are also related at the asset level.
Do not mistake this resource table for the dynamic armor registry.

## Validation / next step

Lua suite, PowerShell parser, in-project Debug CLI build and Core embedded
resource test passed. Manager deployment followed plan/validate/apply.
Production registry remained revision 45 / 135 mappings; protocol SHA256:
`37bee774415f1104312f039bb4290870221e0d1077d0e6fffe7bbc994389a99c`.
The probe's request was stopped after capture.

Next work should establish a specific cross-Unit relationship accessor
contract (HD2's exposed `is_child` is an unvalidated lead), or obtain an
owner/controller reference through a verified native binding. Focus on the
persistent avatar versus the replaced worn assembly and preserve stationary
instances as negative controls. Do not repeat the walking test or trigger
animation/Flow events by guessed names. The missing evidence is a concrete
association and refresh consumer, not another position correlation.
