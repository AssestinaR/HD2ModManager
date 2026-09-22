# Expansion safety investigation

## Evidence

Read-only live run: `evidence/composition-capacity-v040-20260922.jsonl`.
Game stopped and original three-entry profile restored after sampling.

Independent PowerShell reconstruction of the logged addresses and wrapper
lengths confirms the same packed layout in two game launches:

| Kit | Payload bytes | Pieces | Internal free space |
|---|---:|---:|---:|
| RE-1861 | 1576 | 15 | 0 |
| RE-2310 | 2344 | 23 | 0 |
| CE-81 | 2920 | 29 | 0 |

Each payload is exactly 64 + 3*24 + pieceCount*96 bytes. The new live read
also observes another LDLD header directly at each payload end. Increasing
an existing count would traverse another array or the following record,
not newly allocated piece storage. Do not enlarge counts in place.

The v0.4.0 diagnostic converted address strings incorrectly in the game:
its storage array addresses became `0xffffffff`. Its `packed_payload=false`
and empty local backrefs are invalid. Raw chain addresses and wrapper bytes
remain usable and were independently checked by `Inspect-StorageEvidence.ps1`.
v0.4.1 replaces base-16 conversion with digit arithmetic; tests now include
high addresses. The v0.5.0 live run confirms this fix: all three wrapped
objects are packed and local Body/Piece backrefs are their own pointer fields.

An additional low-address CE-81 header copy passed tuple verification but
lacked an LDLD wrapper and pointed at the same original arrays. It could be
a probe buffer; no ownership claim is made. Exact tuples alone do not
identify a write target. The wrapper filter is also not proof of ownership.

Offline inspection of all 411 kits finds 3455 pieces, at most 29 per kit,
11 per body and 3 per body/slot. Observed slots are 0..9 and piece types
0..2. No duplicate body/slot/piece-type key occurs within a kit. These are
observed data maxima, NOT engine limits; repeated keys or new slot IDs
may be rejected or overwritten by downstream fixed-size consumers.

## Proposed stages, not an implemented writer

1. Establish references to the actual relocated objects. v0.5.0 adds an
   undeployed private-region pointer search for wrapped roots after discovery.
   This search is bounded and can include probe-created copies; a hit does
   not establish access, ownership, uniqueness or a safe update point.
2. Identify whether the renderer keeps Kit pointers, Body pointers, copied
   piece records, or derived fixed-size slot arrays. Observe ordinary equip
   and teardown; an update callback alone is not a thread synchronization point.
3. First controlled structure experiment: use the donor's existing full
   composition for a smaller target (15 -> 23), preserving target identity.
   Do not merely map existing Unit IDs. Before enabling writes, establish
   donor resource availability/lifetime, write synchronization and rollback.
4. For genuinely new records, build an independent complete allocation
   before publication. Select the allocator and retention strategy only
   after ownership/destruction behavior is known. Neither Lua GC retention
   nor leaking allocations proves compatibility with engine destruction.
5. Distinguish switching the Body-array reference from replacing the Kit
   root. Which is appropriate depends on consumer caching. Do not change
   every apparent pointer: some may be stale or probe-owned.
6. Only after consumption and cleanup are verified, test one extra piece
   with an existing supported slot/type, then counts beyond observed maxima.
   New slot semantics require a separate consumer investigation.

No memory writer, safe expansion claim, reload call or anti-cheat bypass
was added. Memory/VRAM savings remain unmeasured.

## Recovery and v0.5.0 evidence

The user reported a system shutdown during the previous launch attempt.
There was no surviving new probe log. No cause is established. After recovery,
the agent ran one read-only session which completed after 440 seconds and
stopped only its launched game process. The original profile was restored.

Evidence: `evidence/composition-rootrefs-v050-20260922.jsonl`.

The three wrapped roots share inferred blob base `0x000002c324310000`.
Their offsets exactly match payload offsets in the local plaintext library.
Root references in another private-memory area imply one index base:
`0x000002c04f27c560`. The source indices (zero-based) are CE-81=77,
RE-2310=227, RE-1861=229; reference positions equal indexBase + index*8.
Across the three 96-byte reference windows, 26 distinct neighboring entries
also equal blobBase + the corresponding source payload offset, with zero
contradictions. A low-address candidate has contradictions and is not accepted
as an equivalent index.

This is a strong sequential pointer-index hypothesis, not proof that the
index is consumed by rendering or owns any storage. Full 411-entry reading
was NOT performed in v0.5.0. Some reference contexts no longer matched their
original target when re-read; these were rejected by the offline analyzer.

`Inspect-RootReferences.ps1` checks each reference's context value.
`Inspect-RegistryIndex.ps1` independently correlates indices and neighbors
against the plaintext LDLD framing without writing any game asset.
The v0.6.0 probe adds full index validation derived from runtime LDLD framing,
not a fixed address or hardcoded count. It retains ownership/consumer flags
as false even when all entries match.

## v0.6.0 full-index result

The corrected v0.6.0 run completed after 375 seconds and the original profile
was restored. It found:

- data blob base `0x0000021a993d0000`, exactly `384196` bytes;
- root index base `0x000002170d0bc560`, exactly `411 * 8 = 3288` bytes;
- all 411 runtime root pointers matched the 411 plaintext LDLD payload offsets;
- no index entry was omitted or contradictory.

The three sampled indices remain CE-81=77, RE-2310=227 and RE-1861=229.
This establishes a robust runtime registration snapshot across restarts. It
does not establish the code that consumes the index, whether the array is
immutable after load, or whether a replacement allocation can be published
without invalidating derived caches. The index and blob remain marked
`ownership_proven=false` and `consumer_proven=false`.

The next safe research target is a read-only holder graph: locate pointers to
the full index base and blob base, record page protection and neighboring
objects, and correlate them with an ordinary armor equip/recreation event.
No count, pointer or payload writes should occur before that correlation.

## Holder graph result

The completed holder-graph run found both the full index base and the data
blob base in a private, readable/writable region. The high-address references
are `0xe0` apart. The index reference is immediately followed by `0x19b`
(411). The neighboring blob context contains three aligned pointers at
`-0x08`, `+0x00` and `+0x08`; the middle pointer is the validated data-block
base. This is consistent with a loader/container object that owns a root
pointer array, count and several data chunks.

The holder graph is still only a structural hypothesis. It may be a cache,
package loader or derived index rather than the renderer's live consumer.
Page protection being writable does not make a memory write safe. The next
probe records the exact allocation-region boundaries for these pointers and
the holder qwords; it does not alter them.

## v0.8 holder classification

The read-only probe now adds a structural summary to every holder hit:

- qword offsets containing the validated count `411`;
- qword offsets containing the target index/blob address;
- the number of pointer-shaped qwords in the 192-byte context;
- a conservative `likely_container` flag and score.

The score is only a triage aid (`+3` for count `411`, `+2` for the target
address, `+1` for at least two pointer-shaped qwords). It is not an ownership
or consumer proof. The holder graph's public target list now records addresses
and hex bytes instead of embedding raw binary strings in JSON.

The existing v0.7 evidence remains unchanged and does not contain these new
classification fields. A fresh game run is required to collect v0.8 evidence.
No writer, count change, pointer publication, reload, or destructor hook has
been added.

The completed v0.8 run is retained as
`evidence/composition-holder-v080b-20260922.jsonl`. It found one high-address
index holder and one high-address blob holder in the same private allocation;
their reference fields are `0xe0` bytes apart. The index context contains the
validated index pointer at offset `0`, count `411` at `+8`, and multiple other
pointers. The blob context contains three adjacent data-block pointers at
`-8`, `0`, and `+8`; only the middle pointer is the validated armor blob.

Four additional low-address index hits and four low-address blob hits occurred
inside the `0x7e000000` private region used while the Lua probe was building
strings and log records. They are not accepted as independent engine owners.
Only the unique high-address pair is retained as the current container
candidate. This still does not prove that the renderer reads this object.

## v0.9 temporal holder sampling

After the holder graph is discovered, the probe now takes eight additional
read-only snapshots of high-address holder hits. Each snapshot records the
64-byte holder window and qword decoding, and reports whether any sampled
window changed since the previous resume. This is intended to distinguish a
stable registration holder from a short-lived construction buffer; it is not
a synchronization guarantee and does not exercise equip/recreation events.

The first v0.9 launch did not reach the loader or create a probe log; the game
process remained near 98 MB and was stopped. It produced no runtime evidence.
The active profile was restored to its original three logical entries and
redeployed afterward.

## Independent clone layout

`src/composition_clone_plan.lua` now implements a pure, offline relocation
plan for the first controlled expansion experiment. It:

- preserves the target Kit's first 48 header bytes and identity fields;
- copies the donor Body/Piece structure and all Piece records;
- packs the result into independent storage;
- records the Kit Body pointer and every Body Piece pointer as fixups;
- can relocate those offsets to an arbitrary allocation base for fixture
  verification.

The RE-1861-sized fixture successfully adopts a 23-piece donor structure while
retaining the target item identity. This validates construction arithmetic
only. No allocation, publication, index mutation, synchronization, rollback,
resource lifetime, or rendering behavior is claimed.

## Minimal consumer test

`src/composition_index_swap_test.lua` is a deliberately narrower write test.
It waits for the read-only probe to validate the full 411-entry index, then:

- verifies the target index still points to the discovered RE-1861 root;
- performs one aligned 64-bit store replacing index 229 with the existing
  RE-2310 root from index 227;
- reads the slot back and refuses rollback if another writer changed it;
- exposes `HD2ArmorCompositionIndexSwap.clear()` for guarded restoration.

It does not allocate engine data, edit a Kit, change a Body/Piece count, or
publish the offline clone. Both roots are existing objects owned by the same
validated runtime blob.

The September 22, 2026 launch reached:

```text
applied target=RE-1861 target_index=229 donor=RE-2310 donor_index=227
slot=0x000002150e2acc88
```

The game was intentionally left running for manual armory verification.
Until that visual result is reported, this proves only that the validated
index slot was writable and retained the donor pointer at read-back. It does
not yet prove rendering consumption or safe independent allocation.

Manual verification showed that replacing the entire root did affect the real
consumer, but also replaced RE-1861's identity. Equipping RE-1861 selected
RE-2310, while the shop's RE-1861 preview became effectively absent. The
donor appearance itself rendered correctly. Therefore whole-root aliasing is
accepted only as a consumer proof and rejected as the implementation model.

## Identity-preserving clone test

`src/composition_clone_publish_test.lua` now constructs an independent Kit:

- bytes `0..47` come from RE-1861, preserving its item/archive identity;
- the Body count and all Body/Piece records come from RE-2310;
- every Body/Piece pointer is relocated into one retained LuaJIT allocation;
- publication changes only RE-1861's validated index slot;
- guarded rollback requires that the slot still points at the clone.

The locator now has a fast path. It parses a candidate `0x60000` private
region as exactly 411 LDLD records, validates all 411 index pointers in a
small private region, and publishes the result immediately. It does not rely
on fixed addresses and falls back to the research scan if validation fails.

The live run completed with:

```text
index_base=0x000001fc897bb750
blob_base=0x000002001b920000
matched_entries=411
clone=0x000000007e824f78 bytes=2344 bodies=3 pieces=23
```

The game was left running for manual verification of both armory equip
behavior and shop preview identity. Successful construction/read-back alone
does not establish rendering correctness or cleanup behavior.

## Weight metadata correction

The first identity-preserving clone copied all 96 bytes of each donor Piece.
RE-2310's Pieces have `weight=1`, while every RE-1861 Piece has `weight=0`.
The live result consequently inherited RE-2310's medium-armor value even
though the Item identity and visual rendering were correct.

The clone publisher now treats `Piece +16` as gameplay metadata for this
experiment:

- it requires all target Pieces to share one weight;
- it copies donor Unit/resource/slot/type fields;
- it overwrites each cloned Piece's weight with the target weight;
- it self-checks all 23 cloned records before publication.

The September 22, 2026 run published:

```text
clone=0x000000007e824118 bytes=2344 bodies=3 pieces=23 target_weight=0
```

The game remains running for confirmation that RE-1861 keeps its light-armor
value while using the donor visual composition. This isolates one known
metadata field; other gameplay fields in the Kit header or Piece record may
still require analogous preservation rules.

Manual verification confirmed that restoring the target Piece weight returned
RE-1861 to its light-armor value while retaining the complete donor appearance.

## B-08 passive and shared-Unit test

B-08 (`item 488691438`, index 89) contains mixed target metadata:

- its non-rendering slot-1 Piece has `weight=1`;
- its 18 visible Pieces have `weight=0`.

Sixteen visible B-08 Units are also referenced by FS-38 and CE-74. This makes
the target useful for checking both passive retention and whether per-Kit
composition publication avoids the global pollution caused by Unit resource
overrides.

The clone publisher now preserves two target weight classes independently:
slot 1 metadata and visible Pieces. The live publication completed with:

```text
target=B-08 donor=RE-2310 target_index=89 donor_index=227
clone=0x000000007e8c45e8 bytes=2344 bodies=3 pieces=23
visible_weight=0 metadata_weight=1
```

The game was left running for manual checks of B-08's name, passive, armor
value and donor appearance, plus unchanged appearances for FS-38 and CE-74.
