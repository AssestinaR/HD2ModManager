# Prepared armory-close control experiment

Experiment directory:
`E:\Data\source\repos\WpfApp1\HD2RuntimeArmorOverride\evidence\armory-control-20260926`

## Scope and variants

One target only: RE-1861, item 3340614564. Other 134 prior mappings are
temporarily absent. Original revision-45 protocol is backed up byte-for-byte.
Original profile entries and manager workspace JSON remain unchanged.
A separate loader-only profile supports absent-resource cases through normal
CLI deployment. No unmanaged game files are moved/deleted by the scripts.

All four mapped variants retain the target's original slot-1/body-3 Piece
Unit/type/tone in the protocol. No custom head Unit is injected. Body parts
come from the current Asmodeus custom model, not a newly rebuilt patch.

| Stage | Revision | Total Piece records | body 0 + any | body 1 + any | Patch |
|---|---:|---:|---:|---:|---|
| baseline | 46 | original 15 | original 10 | original 10 | present |
| low_present | 47 | 11 | 7 | 7 | present |
| expanded_present | 48 | 19 | 13 | 12 | present |
| low_absent | 49 | 11 | 7 | 7 | absent |
| expanded_absent | 50 | 19 | 13 | 12 | absent |

Counts include the retained head row. The low variants select type/layer 0
custom pieces; expanded variants add existing type/layer 1/2 pieces.
Thus this is a practical reproduction matrix, NOT a pure count-only
causality test: count and layer composition covary. Missing/present pairs
have byte-identical Piece definitions and differ in deployment/revision.
Installed head/equipment and body type must not change between stages.

## Tools and safeguards

- `scripts/Set-ArmoryControlStage.ps1`: refuses running game/manager,
  checks protocol hashes and profile entry drift, CLI plan/validate/apply,
  reuses Core's real deployed-Unit validator, and atomically replaces only
  the diagnostic runtime protocol. Source JSON is never compiled/modified.
  Resource validation in the production compiler remains intact.
- `scripts/Watch-ArmoryControlTest.ps1`: five fresh game launches, checks
  same-PID ready state, revision, mapping count, retained-clone count/size,
  and unchanged protocol hash. Three user-confirmed open/close cycles
  per stage, no preview/equip after initial baseline target setup.
- Stops on first reported crash or misoperation. Captures status, durable
  action markers and bounded application events. No active native probe,
  debugger attachment, unknown function call or automatic refresh.
- Automatic restoration only with game and manager fully stopped.
  A separate launcher `恢复原配置.cmd` supports delayed restoration after
  ending a surviving crashed process via Steam.
- Original source protocol SHA256:
  `37BEE774415F1104312F039BB4290870221E0D1077D0E6FFFE7BBC994389A99C`.
- Backup profiles snapshot was taken after creating the empty test profile;
  it is audit evidence, not a file to blindly restore over profiles.json.

## Verification

All five protocols pass the actual Lua parser. Both mapped compositions
pass the real composition planner against target metadata from the
manager SQLite index: expected clone sizes 1192 and 1960 bytes.
PowerShell scripts parse successfully. Core's resource validator confirms
all 18 body Unit IDs present or absent as required after deployment.

An automatic clean launch of expanded_absent reached ready: PID 30052,
revision 50, one active mapping, one retained 1960-byte clone. This confirms
the missing-resource diagnostic actually applies, not that armory closure
is safe. No armory interaction was performed in the automated preflight.

A second clean launch of low_present reached ready: PID 41448, revision 47,
one active mapping, one retained 1192-byte clone. Both automated preflight
contexts are saved in the experiment directory. Both games were stopped.
Original deployment and protocol were restored afterwards; original SHA256
verified. The user guide will select baseline only when started.

The manual guide still needs testing by the user. Successful startup,
correct byte size and several successful closes cannot establish lifetime
safety or exclude low-probability failures.
