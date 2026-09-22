# Composition expansion: first stage

## Scope

The goal is changing the consumed armor composition, including adding pieces,
not fitting donor meshes into existing target slots. Temporary read-only
probe deployments were performed; no runtime memory writes. The validated RE-824 override
remains unchanged at baseline commit `5d79418`.

## Verified offline facts

- CLI reports the current index as trusted; its fingerprint is included in
  `evidence/composition-samples.json`.
- The current plaintext cache decodes to 411 kits, not the historical 402.
- Four complete authority samples were exported using a read-only SQLite
  connection after CLI validation.
- All 90 pieces agree exactly between that database snapshot and the plaintext
  Lua decoder: Unit ID, raw body type, slot, piece type, weight, tone variation,
  and auxiliary resource references. IDs remain exact hexadecimal strings.

| Armor | Complete authority pieces | Previously counted visual Units |
|---|---:|---:|
| RE-1861 | 15 | 14 |
| RE-2310 | 23 | 22 |
| RE-824 | 23 | 22 |
| CE-81 | 29 | 28 |

The nonvisual/slot-1 records must not be silently dropped. Their runtime role
still requires confirmation. Neither table is an engine-capacity measurement.

## Serialized schema, not a native ABI

The existing C# reader and the new Lua decoder agree on:

- Kit: 64-byte header; archive at +32; body offset/count at +48/+56.
- Body: 24 bytes; raw body type at +0; piece offset/count at +8/+16.
- Piece: 96 bytes; Unit at +0; slot/type/weight at +8/+12/+16;
  eight auxiliary resource hashes at +24 through +80; tone variation at +88.
- Both offsets are relative to the beginning of the kit payload.

There is no separate capacity field in this serialized layout. This says
nothing about runtime array capacity, pointer relocation, ownership or the
consumer's fixed limits. A synthetic 40-piece parser fixture only validates
the decoder; it is not evidence that the game can spawn 40 pieces.

## Runtime probe status

`src/composition_runtime_probe.lua` is a temporarily deployed candidate sampler.
It searches three item/archive identities and logs bounded context windows.
Its chain verifier checks complete piece tuples under two layout hypotheses;
matching alone does not prove container ownership or consumption.
It does not use the obsolete singleton RVA, write memory, or call a native
armor reload function.

Reads are coroutine-sliced with a finite byte/region/time budget. The current
scan is intentionally incomplete: signatures spanning chunk boundaries can
be missed; late high-address regions may be outside the byte budget; its own
signature/read buffers may match. Logs explicitly mark ownership unproven.
These limitations must be addressed before interpreting a hit as an engine
record. Do not reuse this sampler as a writer or a release locator.

### Follow-up sampling on September 22, 2026

The initial run found 19 isolated signatures. Three high-address archive
matches shared a region whose bytes consisted of repeated 16-byte hash-node
patterns. Their repeated second qword is `ad9c6d9ed1e5e77a`, identified as
`package` by `docs/HD2SDK-CommunityEdition-3.8.0/utils/constants.py`.
The previous self-contamination attribution is withdrawn: these may be a
resource index, but no owner or consumer has been established.

Probe version 0.2.0 changed the search order to readable mapped/private
regions, required an item hit to have a matching archive at the serialized
header offset before chain verification, and scanned all 13,277 readable
regions (7,993,675,776 bytes attempted). It found 13 raw item occurrences,
zero header anchors, and zero complete chains. THIS NEGATIVE RESULT IS INVALID:
full chunk reads exceeded the stale read limit, and header checks used
one-byte-shifted offsets. Skipped regions also counted as scanned.
Version 0.3.0 corrects these bugs and adds cross-chunk/read-failure fixtures,
raw item contexts and separate successful/failed/skipped byte accounting.
Neither a contiguous Kit nor a restructured runtime layout has been ruled out.

### Version 0.3.0 result

The corrected probe found three complete chains in the same private runtime
region (`0x0000019c98750000`):

| Item | Root | Mode | Bodies | Piece counts |
|---|---|---|---|---|
| RE-1861 | `0x0000019c9879397c` | absolute pointers | 3 | 5 + 5 + 5 |
| RE-2310 | `0x0000019c98792f6c` | absolute pointers | 3 | 7 + 8 + 8 |
| CE-81 | `0x0000019c9876857c` | absolute pointers | 3 | 11 + 9 + 9 |

Each chain matched the complete authoritative tuple multiset, including the
nonvisual records and auxiliary resources. The body arrays are separate
absolute-pointer ranges. An `LDLD` wrapper precedes each Kit payload, and the runtime
layout is therefore a relocated LDLD object rather than a simple payload-copy.

This is the first real archive-to-body-to-piece runtime evidence. It still
does not prove that this object is the sole consumer, that it owns the
allocations, or that changing it after construction is safe. The next probe
should rescan this private container, enumerate neighboring Kit objects and
back-references, and correlate it with an equip/recreation event before any
write experiment.

The run successfully read 7,555,457,024 bytes, failed to read 53,444,608
attempted bytes and skipped 1,907,978,240 bytes in oversized regions. It is
not a complete address-space survey. All three matches retain
`ownership_proven=false`, `consumer_proven=false`, `self_copy_excluded=false`.
Version 0.4.0 adds an UNDEPLOYED bounded local back-reference diagnostic,
including four-byte-aligned pointer fields; this does not observe accesses.

The active profile was restored to its original three logical entries after
the run. All runtime probes remain read-only.

Local fixtures exercise empty-region completion, duplicate initialization,
callback restoration, logging/query failures and propagation of original
update errors. They do not verify real Windows memory traversal or gameplay.

## Next evidence gates

1. Validate a bounded runtime Kit -> body -> piece chain against ALL expected
   tuples for RE-1861 and RE-2310; reject the probe's own copies. Record raw
   data separately from inferred field names.
2. Establish who consumes that chain during ordinary equip/recreation and
   whether another cached composition is used instead.
3. Preserve target identity/passive data and substitute only its visual
   definition. Prove the donor's 23-piece composition is consumed where 15
   pieces were previously consumed; registration alone is insufficient.
4. Separately add an extra piece and observe creation, binding, LOD and cleanup.
   Distinguish repeated pieces per existing slot from NEW semantic slot IDs.
   Arbitrary slot numbers may require new consumer logic, not merely arrays.
5. Investigate fixed array limits and ownership before increasing counts
   beyond observed vanilla maxima. Never enlarge a count in existing storage
   without proving capacity.

Hot reload remains later work. Equip-driven teardown and recreation may be a
useful observation point, but no refresh contract has been established.
