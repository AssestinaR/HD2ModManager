# Runtime Composition Cross-Validation / 2026-09-24

## Scope

A temporary read-only Lua probe was deployed through Bingus Shared Loader and
the current manager profile. The probe read the current process only; it did
not write game memory or call a game-native mutation function. The temporary
Loader and probe profile entries were removed after the run, and the previous
managed deployment was restored.

The probe used the same-PID `runtime.status` blob/index addresses as hints,
then independently validated the complete 411-root index, the active protocol
revision, and each selected Kit -> Body -> Piece chain. Nine root substitutions
were allowed only for target IDs listed in the matching active protocol; those
replacement Kit headers retained their original target item IDs. The other
402 index entries matched their original blob roots. None of the five sample
items was a target in that protocol.

## Authority Samples

B-08 was added to `evidence/composition-samples.json` from the current trusted
manager asset index. Its 19 Piece records were compared with the plaintext
armor-set decoder. The expanded five-kit set now contains 109 records, and all
109 match on item/archive identity, raw body type, slot, piece type, weight,
tone variation, Unit ID and auxiliary resources.

The raw body type values are reported as stored. This investigation does not
equate them with Slim, Stocky or Any semantics.

## Runtime Results

All five sampled runtime chains matched their complete authoritative Piece
tuple multisets. Every payload was packed, non-overlapping and immediately
followed by another valid `LDLD` header. The Piece arrays had zero gap before
the next span or payload end.

| Armor | Item ID | Registry index | Pieces | Body piece counts | Payload bytes |
|---|---:|---:|---:|---:|---:|
| B-08 | 488691438 | 89 | 19 | 7 + 6 + 6 | 1960 |
| RE-1861 | 3340614564 | 229 | 15 | 5 + 5 + 5 | 1576 |
| RE-2310 | 820333399 | 227 | 23 | 7 + 8 + 8 | 2344 |
| RE-824 | 4259903063 | 172 | 23 | 7 + 8 + 8 | 2344 |
| CE-81 | 2546928266 | 77 | 29 | 11 + 9 + 9 | 2920 |

The payload sizes equal `64 + bodyCount * 24 + pieceCount * 96`. No spare
in-place Piece capacity was observed. CE-81 is the largest verified sample
at 29 Piece records, not a proven engine maximum or allocation limit.

Full JSONL capture: `evidence/composition-runtime-sample-v5-20260924.jsonl`.

## Probe Changes

- Export and verify five authority samples, including B-08.
- Validate the current process ID, runtime status, matching protocol revision,
  full blob framing and all 411 root-index entries before trusting a locator.
- Permit root-pointer mismatches only for explicitly mapped protocol targets,
  and require each replacement root to retain the corresponding Kit item ID.
- Fail closed by default if the runtime hint is unavailable or inconsistent.
  The legacy unhinted scan remains test-only opt-in; its budget is capped at
  256 MiB.

Lua tests pass, including strict index validation, allowed mapped-root
identity substitutions and rejection of an incorrect replacement identity.

## Limitations

The probe still reports `ownership_proven=false` and `consumer_proven=false`.
These samples establish runtime object shape and observed bounds, not which
engine subsystem owns or consumes each allocation. They do not establish an
arbitrary Unit/slot capacity, safe allocator lifetime, or that 29 is a hard
limit. The separate user-tested identity-preserving 23-Piece remap remains
stronger evidence of visible consumption for that tested case only.
