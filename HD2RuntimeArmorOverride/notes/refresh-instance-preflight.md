# Refresh probe v3: instance preflight

The first guided capture (session 86e2a3d330b745ea93fb8557bfc000a1)
completed 279 snapshots, each 500 entries / 13 tables, with no observed member
changes. It enumerated API tables, not player instances. Application was not
traversed by the keyword filter. V2 adds explicit API roots.

Native reads are opt-in and staged within a single handshake session:

1. `worlds`: Application.worlds(), then World.num_units(world).
2. `units`: allowed only after a successful World phase in this session;
   World.units(world), Unit.alive(unit), Unit.name_hash(unit).

Reference contracts (Autodesk original engine, not an HD2 ABI guarantee):
- https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/lua_ref/ns_stingray_Application.html
- https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/lua_ref/obj_stingray_World.html
- https://help.autodesk.com/cloudhelp/2021/DEU/Max-Interactive-Help/lua_ref/obj_stingray_Unit.html

Application.main_world is exposed as a debug helper selecting the world
rendered last frame with most units. It is listed but not called or used as
proof of local player ownership. Unit.name_hash returns a representation
usable to spawn the same type; the probe records 16-digit hex strings or
exact raw eight bytes with distinct encoding labels. Observed 16-byte
`#IDPRE_!` strings go through the engine's `IdString64.to_hex`, not guessed
endianness. Other representations are recorded with type/length/bounded raw
bytes and marked unresolved; they do not stop otherwise valid enumeration.

Limits: 128 world-list entries, 4096 units per world, 32 units per tick/batch, queries at
0.1s (units) or 0.5s (worlds), existing phase/file limits. Arrays are reacquired
each tick; no native handles persist between ticks. Identity tokens are only
session-local hints: native wrapper reuse/recreation is not proven. Partial
scans do not establish deletion. World-list truncation is explicitly reported.
`worlds_validated` means the API-access preflight passed, NOT that all worlds
were covered or the player world was identified. Ordinal cursors cannot prove
stable membership while the engine changes its lists. Wrapper token changes
do not reset the Unit cursor; count changes still do.
One whole-array native call can exceed the
soft CPU budget. pcall catches Lua errors, not native access violations.
The first call to each API is logged and flushed before entering native code.
Any unexpected return type or query error stops native queries for that phase.

Run `scripts/Start-ArmorRefreshPreflight.cmd` in the ship after deploying v3.
It runs two ten-second phases without asking for armor changes. On failure,
send the result directory; do not repeat the full eight-phase guide. The
full guide also enforces these gates before any equip instructions.

V3 is not yet proof of a safe refresh method and never invokes mutation APIs.

## September 25 automated verification

The user's `233444-b0a929...` run stopped at our 8-world cap, not at a failed
native call. Raising the bounded enumeration allowed World queries.
The next run then exposed our overly strict hash representation assumption.

Final capture:
`evidence/refresh/20260925-234615-17c2f30069274b6c97c5ef8d05406331`

- PID 8364; production protocol revision 45; 135 mappings unchanged.
- 11 world-list entries, no list cap hit; Unit phase reached all 11 ordinals
  and began a second cycle. This is not an atomic snapshot.
- 2,383 live Unit observations (not unique instances); 2,361 decoded hashes;
  zero native query errors. Longest recorded individual call ~0.254 ms.
- 399 distinct decoded resource hashes; 12 exactly match custom model Unit
  IDs in the active production protocol (`5236bd49853b...`).
- 22 observations remain unresolved with prefix `#IDPRE_"`; retain raw data
  rather than assume that tag has identical semantics.
- The first world held approximately 2,204 Units. Do not assume it is the
  local player's authoritative world merely from size or index.
- Wrapper identities can change despite unchanged world contents. Never
  treat wrapper-token churn as spawning/destruction evidence.

Next: user-guided equip capture, correlate resource-hash distributions and
cycle/coverage across phases. Existing observations prove instance access,
not the identity of the refresh controller or a safe refresh method.
No spawn/destroy/equip/refresh function has been invoked.

All Lua tests and in-project Debug builds passed. Deployment used the active
profile without recompiling mappings. Protocol SHA256 stayed
`37bee774415f1104312f039bb4290870221e0d1077d0e6fffe7bbc994389a99c`.
