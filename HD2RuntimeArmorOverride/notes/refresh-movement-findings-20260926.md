# Successful movement capture — September 26, 2026, 09:41

Evidence: `evidence/refresh/20260926-094130-8e2098461cfc4c388675b7376bff62fd`

The user confirms both actions succeeded; blank notes are not failed actions.
PID 67336, production revision 45. This analysis does not restart the game,
deploy a patch, or modify any production protocol or Lua.

## Coverage

| Phase | Live/positioned Unit observations | Custom-resource observations | Native errors | Truncated |
| --- | ---: | ---: | ---: | --- |
| Spatial preflight | 6,217 | 849 | 0 | false |
| Still | 6,446 | 888 | 0 | false |
| Walk | 6,233 | 781 | 0 | false |

These are repeated observations, not unique instance counts. Eleven Worlds
were enumerated without a list cap. Only the selected largest World's tail
was position-sampled; this is not whole-scene coverage. Largest observed
World counts were 2,206 / 2,202 / 2,209 respectively.

The moving group's median revisit interval in the walk phase was 0.405 s
(maximum 0.524 s), compared with missing post-cue coverage in the previous
capture. All 12 resource IDs were observed 25 times each in the walk phase.
Maximum individual native-call time was 0.303 ms; this is not total probe
frame cost. Logs stayed under the 4 MiB/12,000-record limits.

## Motion discriminates the three same-resource groups

These ordinal windows describe this capture, not persistent addresses:

| Group | Still phase | Walk phase |
| --- | --- | --- |
| 2043–2054, 12 custom Units | At (-0.48420, 0.86848, 20.01000), drift under 0.000004 | All end at (-0.20342, -8.44812, 20.00289), displacement 9.32084 |
| 2164–2175, same 12 resources | (1.4704, 13.8690, 19.5700) | Unchanged |
| 2177–2188, same 12 resources | (-3.1959, -1.2841, 20.3135) | Unchanged |

Distances are engine coordinate units, not an independently verified meter
scale. The cue occurred about 3.3 s after acknowledgment, not an exact
engine event timestamp. The first custom anchor was unchanged at 3.585 s,
first observed moving at 3.990 s, and settled by roughly 7.025 s.
The other two groups remained fixed throughout their observations.

User-confirmed walking plus this selective motion strongly identifies the
first group as the current player's worn visual assembly in this session.
It does not establish a universal multiplayer local-player selector.
Other unrelated scene objects also moved; "any moving Unit" is not a
valid selector. World size, array ordinals and fixed coordinates are not
production lookup rules.

## A concrete persistent avatar candidate

Resource `4d1c334d294dfa97` (decimal `5556372446766824087`) is labeled in
`docs/HD2SDK-CommunityEdition-3.8.0/hashlists/friendlynames.txt` as:

`content/fac_helldivers/cha_avatar/avatar_helldiver`

At ordinal 2015 it retains probe-local token `userdata:639` throughout
this session and follows the same start/end positions and motion trajectory
as the custom group. It was observed 26 times in each action phase.
Its positions were read approximately 30–63 ms before the first custom
anchor, so differences while walking (up to about 0.186 coordinate units
with nearest-time matching) must not be mistaken for a static attachment
offset. These reads are not simultaneous.

Cross-check against the earlier four confirmed equip transitions:
`evidence/refresh/20260926-000859-5909fde880da4338b0c98203ab883e77`

| Resource | Earlier session token behavior | Current movement evidence |
| --- | --- | --- |
| `4d1c334d294dfa97` | Stable `userdata:720` across all phases | Same trajectory as worn custom group |
| `40ae90fff236865b` | Stable `userdata:754` across all phases | Same start/end position and movement |
| `d40f23a128bd988d` | Stable `userdata:758` across all phases | Same start/end position and movement |
| `8a026ed4c1830deb` | Tokens change on all four equip actions | Moves with worn group |

The last resource is labeled `Helmet O-3 Free Spirit` by the local name
table. This is metadata corroboration, not independent runtime type proof.
Identity tokens are session-local userdata observations, NOT native
addresses. Do not compare token numbers across game launches or infer
native destruction solely from token changes.

## Conclusion and next step

The worn group is now motion-validated; another identical walking capture
is unnecessary. There is a stronger owner-side investigation anchor:
the named avatar resource that moves with the player while retaining its
observed identity across normal equipment swaps. This is not yet proof
that this Unit owns the assembly or implements its controller.

Next inspect verified read-only attachment/component references from a
freshly enumerated avatar instance to the observed worn Unit group.
Prioritize exact resource IDs plus relationship validation, not static
ordinals or broad movement heuristics. Use the stationary same-resource
groups as negative controls. If no suitable engine accessor is available,
document that boundary before choosing a different instrumentation route.

No refresh function, owner pointer or native destruction/recreation path
has been demonstrated. Do not call guessed update functions or manually
destroy/recreate the avatar or armor as a shortcut. A later isolated
refresh experiment must prove target-specific effects without changing
equipment identity, gameplay attributes, preview objects or other actors.
