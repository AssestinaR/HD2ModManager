# Armor registry route review / 2026-09-22

## Scope

Review of local probe source, existing findings, loader source and latest V9
log. No game launch, deployment, process-memory writes or Lua edits in this
review. Historical disassembly conclusions were read, not independently
re-derived.

## Conclusion

Runtime appearance remapping remains a plausible research goal, not a
demonstrated implementation. Neither the final writable armor mapping nor
its model-refresh contract has been established. Finding all archive IDs is
not a prerequisite for proving one useful remap.

The next milestone should be one target armor using one donor's compatible
visual definition through the game's normal equip/create lifecycle, while
preserving target gameplay identity. Live replacement of an already spawned
model is a separate, later milestone.

## Evidence Corrections

1. `scan_pointer_targets` searches a 32 KiB window centered on a candidate
   pointer. Matching all known donor Units in that window proves byte
   proximity, not object membership, ownership or an Archive-to-Unit edge.
   Matching only known IDs also does not establish the complete set: unknown
   IDs are not enumerated by this test.
2. Archive contexts cover up to 16 KiB. Repeated pointers across overlapping
   contexts do not establish that separate Archive objects share ownership.
   The first occurrence of each Archive is the only one expanded, so a
   non-consumer occurrence can suppress a more useful candidate.
3. The probe creates binary signatures and copies scanned memory into its
   own process heap. It must rule out its own signatures, buffers and prior
   outputs before treating subsequent matches as engine records. This is a
   contamination risk, not proof that the historical matches were false.
4. `scan_pointer_targets` is defined before `local archive_patterns`.
   Its reference therefore resolves to a global, not that later local.
   Under the ordinary environment its Archive scan sees an empty table.
5. `u64` copies eight bytes now, but converts arbitrary values to Lua numbers.
   Preserve hashes as exact bytes or uint64 cdata; do not route 64-bit IDs
   through floating-point numbers. The byte-pattern matcher already uses
   exact bytes. Valid Windows user pointers in the current plausibility
   range are a separate case from arbitrary 64-bit hashes.
6. The latest fallback centers on `0x00007ffb8ec40000` with a 2 GiB radius;
   that does not cover the historical low heap address range. The scan also
   skips any region whose selected size exceeds 16 MiB rather than chunking
   it. Three runs each read 71,933,952 bytes across 12 regions and find zero
   signatures. This is insufficient coverage, not evidence of encryption
   changes or removal of the armor cache.
7. Advancing 512 qwords from `object + 0x948` does not prove a 512-entry
   array. Pointer/inline storage, count, bounds and ownership must first be
   derived. Old generic resolver names and strides remain hypotheses about
   the armor consumption path until linked to a real equip operation.

## Recommended Next Experiment

Use two visually distinct original armors, A and B, plus an unaffected
control C. Establish their current static slot/body-type/Unit definitions;
the existing index was described as stale and is not automatically authority
for an updated game.

First collect a bounded inventory of already-loaded Lua module names and
relevant exposed table/function names. Do not recursively traverse the
whole Lua environment, require unknown modules, call unknown native
functions, or assume a particular Stingray API exists in this fork.

Use real A -> B -> A equip/preview actions to correlate:

- Selected cosmetic identity separately from armor gameplay identity.
- Unit IDs and body-type/slot selection used for new visual instances.
- The operation that creates or replaces those instances.
- Available package/resource acquire/release observations.

If these operations are not exposed in Lua, resume offline native consumer
analysis using the static Unit definitions and existing resolver evidence.
Do not expand arbitrary heap scans indefinitely. Native calls require a
verified ABI, calling thread and lifetime contract before any invocation.

The acceptance gate for a candidate is an explicit owner/field/container
chain with validated bounds, independently verified A and B Unit lists,
and evidence connecting it to actual visual selection/creation. Mere
spatial proximity or an ID-count threshold is not sufficient.

## Subsequent Gates

1. After the read-only gate, separately authorize a reversible, single-target
   visual mapping change. Prefer the ordinary equip/recreate path over
   inventing a native reload call. Do not modify donor shared contents or
   copy GPU pointers independently of the owning resource system.
2. Confirm A displays donor B while C and target gameplay identity remain
   unchanged; test both body types, relevant LODs, repeated equip and a scene
   transition. Abort on uncertain schema or version.
3. Compare cold launches with redundant donor copies removed from the test
   package. Record identical scenes and settings, CPU memory, dedicated GPU
   memory and available resource identities/counts. Appearance success,
   resource reuse and reduced residency are separate results.
4. Only then add release/reload behavior for existing instances. An unused
   old resource may remain resident; remapping alone does not prove that
   the engine releases it or that peak startup memory falls.

## Update Compatibility

Prefer a verified semantic Lua entry point if one exists. Otherwise use a
build fingerprint plus validated native function/global relationships and
schema checks. Cache module-relative locations, never absolute heap
addresses. Unknown builds should fail closed, not silently run broad scans
or reuse an unchecked old offset.

Current data does not yet establish old/new structural equivalence. A null
old RVA and zero hits in an incomplete fallback do not identify what changed.
Low maintenance is a design target, not a promise of update independence.

## Safety And Claims

The local loader demonstrates an existing Lua loading route. It does not
prove official endorsement, anti-cheat compatibility for arbitrary FFI
operations, or future account safety. Do not bypass GameGuard or treat a
successful read-only run as approval for native writes.

Disk encryption and startup hash-check explanations also remain hypotheses
unless independently established; a crash after file replacement does not
identify its cause.

## Reviewed Sources

- Historical exploratory files under the repository's ignored `tmp/` folder
- `docs/BingusSharedLoader-main/src/shared_loader.lua`
- Local loader logs: `ArmorRegistryProbe_runtime_v9.log`
