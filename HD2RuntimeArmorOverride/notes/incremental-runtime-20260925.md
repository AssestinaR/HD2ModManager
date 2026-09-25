# Runtime 0.2.0: incremental application and lifetime safety

## Unchanged external contract

The manager still writes the complete schema 1/2 protocol. Lua computes a
canonical, pointer-free final Kit/Body/Piece payload per target. Header identity
comes from the original target, weights use the existing scoped majority
resolver, and custom tone remains at Piece +88. No gameplay or helmet policy
change is intended.

`armor_registry_composition.lua` plans payloads from checked, immutable memory
snapshots. Resource IDs never pass through a floating-point Lua number.
`armor_registry_transaction.lua` owns native clone references and incremental
publication. Both modules are embedded in the manager's Core assembly and the
standalone addon builder.

## Application

- Text-equal polls return without parsing or rebuilding. A changed protocol
  computes effective payloads, but identical payloads reuse native buffers and
  perform zero native writes, even if revision/comments/order changed.
- Effective changes build only new target compositions. Removing a mapping
  restores its validated original pointer. A/B/A switches can reuse A's retained
  buffer. Irrelevant overrides and unused models do not rebuild other targets.
- Snapshots are shared within a transaction, not cached indefinitely. Source
  and target memory is revalidated after time-sliced preparation, before writes.
- Locator, preparation, and optional audit work share a soft 1 ms/frame budget.
  Several scan steps can run in one frame, replacing the old one-region/frame
  behavior. Native calls and the final validation/publication are indivisible;
  this is not a strict worst-case latency guarantee.
- Unpublished work can be superseded by newer protocol text. A final file check
  catches changes between polling ticks.
- Invalid input is logged once per changed text. Source-snapshot drift receives
  at most three attempts with backoff. Permanent failures wait for new input.
  Ordinary Lua errors do not suppress the original game update callback.

## Ownership / failure boundaries

All potentially published buffers are strongly retained before the first
pointer write, including buffers exposed during a failed transaction. There is
no GC-based release or timer-based reclamation of published objects. The pool
has a 32 MiB native-payload limit and an 8192-object limit; exhaustion rejects
new compositions with a restart-required error. Existing mappings are retained;
removal and reuse remain possible. Canonical signature strings also consume
memory, so reported retained bytes are NOT total Lua memory or GPU memory.

Publishing checks slot ownership and writable memory, checks the expected
pointer again before each write, verifies writes, and attempts guarded rollback
including the failing slot. Failed rollback blocks subsequent publication;
restart the game. Buffers remain pinned in all cases.

This is NOT engine-wide synchronization, not an atomic multi-target
transaction, and not proof that other native consumer caches refresh safely.
Another engine thread can race validation; underlying engine-owned resources
may have lifetimes beyond the registration object. These limitations require
real game stress testing. No GameGuard compatibility or crash-free guarantee
is made. Invalid original pointers fail closed rather than guessing replacement
addresses while mappings remain live.

## Diagnostics / validation

`runtime.status` reports `runtime_version=0.2.0`, `retained_clone_count`,
`retained_clone_bytes`, `last_built`, `last_reused`, `last_removed`, `last_writes`,
`last_prepare_ms`, `last_publish_ms`, and `locate_ms`. Preparation/locating times
are elapsed wall time including yields, not CPU time.

Automated tests:
- no-op, partial change, removal, reversion, pool limits, GC retention;
- write-then-fail, rollback failure, stale transaction and external slot change;
- exact 64-bit IDs, target header/weight preservation, custom Body ordering;
- shipped runtime against synthetic process-local FFI memory: full discovery,
  80-target updates/no-ops, superseding in-flight work, invalid source memory,
  invalid protocol and failed custom composition, original update continuity.

Manual acceptance:
1. Exit the game, redeploy the active profile with the updated manager, restart.
   Reloading protocol alone does not update the Lua executable code.
2. Verify `runtime_version=0.2.0`.
3. Try ordinary and custom sources, both body shapes, first-person view, and
   target armor stats/passives. Keep existing helmet safety rules.
4. Change one target or a shared source component: only affected targets should
   have writes. Reapplying the same effective state should report writes=0.
5. Repeat A/B/A switches and removals; retained pool should stop growing when
   revisiting identical effective compositions.
6. Exercise ship/armory/mission transitions and a long session. Collect status,
   runtime log, and crash evidence if an issue recurs.
