# Diagnostic B: exact donor Piece templates

This is an opt-in diagnostic, not a production protocol extension for the GUI.
Normal mappings and custom models without `test_piece_template` are unchanged.
The Lua serializer explicitly rejects diagnostic models rather than silently
dropping their provenance. Do not apply/compile the GUI workspace during testing.

## New evidence

Read-only queries of the manager asset index match all eight visible Lunalice
body Units to item 820333399 (RE-2310). Their native `piece_type` is **1**, while
the converted custom definition assigns **0**. The helmet donor is item
1745464610, body 3, slot **0**, unlike the custom body composition's slot 1.
These differences warrant testing, but do not alone establish the root cause.

## Experiment

- Target: RE-824, item 4259903063, only.
- Package: existing Lunalice custom output ending `211207-a3a046`, unchanged.
- Eight body Units; no custom helmet injection in this experiment.
- Fast, frame-budgeted locator remains enabled.
- Each diagnostic mapping names the donor item, body and exact original Unit.
- Snapshot the donor through the same checked reader and publication guards.
- Copy its complete 96-byte Piece. Replace only Unit ID and target weight.
  Preserve native slot/type, tone, auxiliary hashes and unknown bytes.
- Body/slot mismatch, missing/ambiguous donor and missing provenance fail closed.
- Retain the target Kit header/identity. Other targets are not mapped.

This deliberately changes the previous all-armor/helmet test scope. Success
would support the donor-template approach, but would not isolate type vs
auxiliary fields, nor prove helmet compatibility or all-target stability.

## Deployment and rollback context

Backup directory:
`C:\Users\Administrator\AppData\Local\Temp\hd2-template-b-20260925-215126`

It contains the pre-test `profiles.json`, original revision-31 protocol, and
the diagnostic revision-32 protocol. Manager workspace JSON is unchanged.
The active profile was switched using CLI from original-ID Lunalice node
`09508619-0c84-4f28-b813-389b0ea175fa` to custom-ID node
`6d2f033e-5dfe-4cd6-96c4-349ab7b54895`; both library Mods remain intact.

For rollback, exit the game, use CLI to restore those profile entries, restore
the saved protocol, and redeploy. Do not blindly overwrite a subsequently
edited profile with the backup.

## Validation

Lua suite passes, including exact preservation of donor bytes except Unit and
weight, actual runtime template resolution, and missing-donor rejection.
Manager and CLI Debug builds succeed with existing warnings. The deployed Lua
was decoded and compared against all three modified source modules.
No game launch or visual validation has been performed for B yet.
