# Armory close crash: joint investigation with appearance refresh

## User report

Intermittent on the developer machine, frequent on some users' machines.
Suspected relationship: source visual has more Unit slots than target.
This is a hypothesis, not a diagnosed cause or proof of a hardware fault.
Earlier helmet/layer experiments are useful leads, not proof that all
armory-close crashes share the same cause.

## Code facts checked this turn

- `armor_registry_transaction.lua` pins every published clone before
  exposing its pointer, including failed publication attempts. Retired
  clones remain in the pool for the Lua state's lifetime.
  Therefore ordinary collection of those buffers is not the first
  explanation; engine-owned instances and Lua-state teardown remain separate.
- `publish_document` explicitly has no engine-wide synchronization.
  Non-yielding Lua publication is not proof that native consumers cannot
  overlap it or observe multiple target replacements at different moments.
- Custom compositions can copy per-position target templates or use
  zero-filled 96-byte Piece templates when no matching position exists.
  Valid serialized counts do not prove every consumer tolerates every
  slot/type/body/auxiliary-field combination.
- The existing owner_native probe observes each token once per phase.
  It is NOT a continuous teardown trace and cannot demonstrate that no
  stale links occur during armory closure. Its known-resource allowlist
  also does not cover arbitrary users' model packs.

## Competing explanations to distinguish

### Refined user report, September 26

The user reports crashes on closing the armory without preview/equip actions
when mapped source slots exceed the target. Their one local reproduction
used custom Unit IDs whose supplying patch was absent. Other users' exact
configurations and frequencies are not yet available; do not generalize
the source-count condition as a verified universal trigger.

This reduces the priority of heavy mesh/GPU content and in-session Apply
as necessary causes. It does NOT rule out resource lookup failure: missing
Units may leave partially initialized cleanup records. The leading
distinction is now expansion bookkeeping versus incomplete-construction
cleanup, including an interaction between them.

Inspect the exact runtime protocol, not only manager source JSON:
`RuntimeArmorResourceFilter.Compile` filters unavailable custom Units and
rejects mapped empty models. A rejected compile deliberately preserves the
previous runtime protocol. `prepare_document` in Lua consumes the resulting
Piece definitions without independently verifying successful engine Unit
instantiation. Missing-patch tests must record protocol hash, applied
revision, actual effective composition and deployed resource inventory;
otherwise a stale protocol/old mapping can invalidate the comparison.

Use a clean-launch 2x2 experiment, one target and one fixed part/body/helmet
configuration: no expansion vs expansion, supplying patch present vs absent.
Add mapping-disabled control. Do not change deployment in a running game
or weaken release resource validation. Missing-resource cases, if needed,
must be explicit isolated diagnostics with restoration of protocol/profile.
Keep non-expanded and expanded cases both nonempty; zero Pieces is a
different rejected configuration. Count effective Pieces per selected
body/part and distinct slot/type values separately.

1. Expanded effective pieces/slot layout conflicts with instance cleanup
   bookkeeping, despite valid registry serialization.
2. Particular slot/type/body/helmet composition violates consumer
   assumptions; count is only correlated.
3. Registry publication overlaps preview/equipped-instance replacement,
   producing mixed lifetimes or snapshots.
4. Engine resource ownership/destruction differs for preview and avatar,
   including shared Unit resources; retained registry memory alone is insufficient.
5. Unrelated patch, loader, driver or engine failures. Machine-dependent
   frequency does not distinguish these from timing-dependent defects.

## Controlled experiment order

Use the same source pack, game/loader versions, target, body type and settings.
Keep one protocol fixed throughout each run and restart between variants.
No automatic refresh experiment until this is better understood.

1. Mapping disabled control, same model packs installed.
2. Mapping enabled at startup; no in-session Apply. Open/close without
   switching, preview only, then equip-and-close as distinct actions.
3. Compare compatible layouts with fewer/equal/more effective pieces per
   active body, holding helmet behavior and piece types fixed where possible.
   Raw total Unit count across stocky/slim/any is not the effective count.
4. Separate helmet and nonzero-layer changes, one variable at a time.
5. Only then compare in-session protocol updates outside/inside armory.

Collect successful and failed runs with operation counts; do not infer
stability from one successful close. Reproduce with and without the probe
because instrumentation changes timing. Do not automatically disable parts,
remove objects, change equipment stats, or deploy guessed cleanup calls.

## Evidence improvements implemented

The guide now saves runtime/probe status and protocol hash at start,
before each guided action and at exit/error. Durable markers record the
capture acknowledgement and cue BEFORE waiting for completion, so a crash
does not erase the last operation's timing. A cue is not proof of user action.

`Save-ArmorRefreshContext.ps1` optionally collects a bounded Application
event window restricted to helldivers2.exe reports; PID correlation is
explicitly unverified. Missing/late WER events or a surviving process do
not exclude a crash. No debugger, dump configuration, registry edits or
new native calls were introduced.

Still needed: bounded repeated avatar/preview association snapshots and
composition summaries correlated to publish begin/end, without stale
handles or unbounded per-frame logs; an exception stack/dump if obtainable
without circumventing protection. Event fault-module names alone cannot
identify the failing cleanup owner.
