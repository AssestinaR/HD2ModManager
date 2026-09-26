# Refresh v4: shorter, interleaved candidate capture

## Changes

- Guide: 10 seconds per phase (was 20), 3-second countdown (was 5).
  The countdown is inside the capture, leaving approximately 7 seconds after
  the cue. Users can still specify `-CaptureSeconds 20`.
- `focus` is a new explicit read-only mode gated by a successful World
  preflight in the same handshake session. Guide requires probe version 4.
- One world per native tick, scheduled at >=10 ms intervals (actual rate is
  limited by update frequency). Worlds are interleaved, not held behind a
  full 2,200-Unit world scan.
- At most 32 Units per tick with the existing soft 0.5 ms budget. Large
  worlds scan only their last 256 ordinals. Smaller worlds scan all entries.
  This is a session-derived candidate window, NOT a general player locator.
  Every batch reports its lower coverage boundary and `focus_tail=true`.
- Separate numeric cursor per world ordinal; counts changing reset that
  world's cursor. No native handles persist in those cursors. World reorder
  can still mix observations, so no deletion/ownership claims are allowed.
- In focus mode `cycle` counts world round-robin passes, NOT completed
  scans of every world's candidate window. The batch indices express actual
  coverage. Do not compare cycle counts as full snapshots.
- `Unit.id(unit)` is explicitly allowlisted and tested. Record nil as nil,
  not an error or an invented stable ID. Metadata/ordinary units mode does
  not call it. No parent or mutation APIs were added.

## Automatic game verification

Evidence:
`evidence/refresh/20260926-000414-b0cf934de35746c8a49665cc95cab245`

- World and 10-second focus phases passed; 1,810 live Unit observations,
  1,803 resolved resource hashes, zero native errors.
- 11 world ordinals were visited, roughly 60 round-robin passes.
- Large-world candidate window: ordinals 1951..2206.
- First observed custom group at ~1.54 seconds rather than ~12 seconds.
  Example resource `5236bd49853b0001` at ordinal 2043 was observed at
  ~1.54, 5.11 and 9.15 seconds, with the same session-local userdata token.
  Other same-resource groups appeared at 2164 and 2177 with different tokens.
- `Unit.id` returned nil on ALL 1,810 observations. It does not provide
  stable instance IDs for these candidates. Repeated wrapper tokens are
  supporting observations only; destruction/recreation/address reuse is
  not solved by this test.
- Individual native call max ~0.179 ms; total probe overhead is not measured.
- Production protocol SHA256 unchanged:
  `37bee774415f1104312f039bb4290870221e0d1077d0e6fffe7bbc994389a99c`.
- Updated runtime deployed via CLI to the unchanged active profile. Game
  restarted for deployment; sampling stopped after preflight.

## Contract check and next inference boundary

Autodesk original Unit API reference:
https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/lua_ref/obj_stingray_Unit.html

`Unit.id` is an editor/level ID; its documented dynamic-spawn result is nil.
This is consistent with our observation, but is not independent proof of
how Arrowhead created these objects.

`Unit.scene_graph_parent(unit, node_index)` returns a NODE index within the
Unit scene graph, not an owner/parent Unit. Do NOT mistake it for the
cross-Unit attachment query proposed in earlier exploratory notes.

Next user capture uses the shorter guide. Compare repeated observations of
the known custom-resource groups before/after cues, including wrapper-token
stability as a hypothesis rather than established native identity. If this
does not identify a stable control object, do not increase broad scans or
invoke a guessed refresh API; investigate documented cross-Unit ownership
or native consumer references separately.
