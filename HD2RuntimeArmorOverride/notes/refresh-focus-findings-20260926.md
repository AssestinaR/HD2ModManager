# Focus capture findings — September 26, 2026

Evidence: `evidence/refresh/20260926-000859-5909fde880da4338b0c98203ab883e77`

User confirms ALL eight actions succeeded. Blank notes mean no additional
comment, not missing/failed actions. Preserve the original session file.
CW-36 / FS-23; PID 50468; production revision 45 throughout.

## Quality and scope

- Eight action phases: 17,255 live Unit observations, zero native errors,
  no world-list caps. These are repeated observations, not unique instances.
- `close_armory` has a truncated metadata snapshot: sample 16 began at
  9.909 seconds and had not completed at the deadline. Native summary and
  phase_end are present. Do not interpret this flag as a lost whole capture.
- No changed Lua metadata members. Unit.id returned nil on all observations.
- Tail windows only; world ordinals and Unit enumeration ordinals are not
  durable identifiers. Round-robin `cycle` is not a complete window snapshot.

## Repeated, selective object-token replacement

Compare the 12 custom resource IDs in the equipped-model candidate region
(large-world ordinals approximately 2042..2055) before 3 seconds and after
8 seconds. All 12 IDs were observed on both sides in each action phase.

| Action | Equipped candidate token changes | Preview candidate |
| --- | ---: | --- |
| idle | 0 / 12 | absent |
| open_armory | 0 / 12 | appears |
| preview_b | 0 / 12 | 12 / 12 change |
| equip_b | 12 / 12 | 0 / 12 change |
| equip_a | 12 / 12 | 12 / 12 change |
| equip_b_repeat | 12 / 12 | 12 / 12 change |
| equip_a_repeat | 12 / 12 | 12 / 12 change |
| close_armory | 0 / 12 | no longer observed after closing |

First equip_b is particularly informative: B was already previewed, so
confirmation changes the equipped candidate without changing the preview
candidate. The later A/B actions include selecting a different preview.

The two other same-resource groups at ordinals 2164..2175 and 2177..2188
retain all 24 corresponding tokens across the entire eight-phase capture.
Nearby non-custom objects at ordinals 2038..2041 and 2056..2060 also retain
their observed tokens. Thus this is not indiscriminate whole-scene wrapper
churn. Two non-custom resources interspersed with the 12 custom resources
also change tokens on equip; their ownership/role is not established.

Example resource `5236bd49853b0001` in the equipped candidate:
`userdata:760` -> `2851` -> `3354` -> `3731` -> `4146`.
The corresponding two control-group tokens remain `941` and `959`.
These are probe-local labels, NOT native addresses or engine IDs.

## Timing and transient behavior

- Cue is ~3.3 seconds after acknowledgment, not an exact game event timestamp.
- preview_b: last old custom tokens ~4.261 s, first new ~4.639 s.
- equip_b: some old/new observations straddle 4.09..4.28 s. Other components
  were not revisited until ~6.95 s. Enumeration can mix generations within
  a window; never label such a batch an atomic model composition.
- Subsequent equipped groups first observed new around 6.69..7.04 s,
  with old observations around 3.94..4.26 s. This bounds observation, NOT
  an actual 3-second refresh delay or preview-before-equipped execution order.
- Opening armory briefly produced smaller-world counts 20, 36, 52, 81, 95,
  then the next phase settled at 35. V3's slower scan missed this transient.
  Do not mistake these short-lived observations for a persistent leak.
- Closing showed 35/36 -> 20 and stopped showing custom resources there;
  equipped candidate tokens remained stable.

## Conclusion / next step

This is strong evidence separating a preview-model candidate from an
equipped-model candidate, and of selective instance/wrapper replacement
associated with equip. It still does not prove native object destruction,
identify local-player ownership or identify the refresh controller.
Weak-key identity tokens do not solve native-address reuse/GC semantics.

Do not ask for another identical A/B capture: four equip transitions plus
preview/close controls already establish the pattern. Next work should
identify the persistent owner/controller of the changing group and validate
a read-only cross-Unit relationship or component reference. Nearby stable
objects are search candidates only; ordinal adjacency is not ownership.
Unit.id (nil) and scene_graph_parent (node relation, not owner Unit) should
not be pursued as if they supplied the missing owner.

Before any refresh experiment, demonstrate an owner-specific normal visual
update path that does not change gameplay equipment/stats, affect preview or
other actors, or manually destroy/recreate unknown model objects.

Analysis only: no production Lua/protocol edits, deployment, game restart,
or mutation calls during this turn.
