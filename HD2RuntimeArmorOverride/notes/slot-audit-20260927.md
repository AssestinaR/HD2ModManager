# Runtime Kit slot audit, 2026-09-27

## Scope and reproducibility

One read-only game capture in `../evidence/slot-audit-20260927-193745/`.
`slot-snapshot-36552.jsonl` has a complete start/end pair for PID 36552,
protocol revision 64, stable registry index, 411 original Kits and 135
published clones. The active configuration mapped all 135 armor Kits to one
offline custom model. The probe was undeployed afterward; the game and manager
were stopped. The evidence files are local and not committed.

Reproduce the comparison with:

```powershell
python HD2RuntimeArmorOverride/scripts/analyze_registry_slots.py `
  HD2RuntimeArmorOverride/evidence/slot-audit-20260927-193745/slot-snapshot-36552.jsonl `
  --protocol-status HD2RuntimeArmorOverride/evidence/slot-audit-20260927-193745/protocol-status.json `
  --output HD2RuntimeArmorOverride/evidence/slot-audit-20260927-193745/analysis.json
```

## Observations

- The protocol specifies 19 enabled Pieces. All 135 published Kits have those
  exact body/slot/type/Unit tuples. Their target identity/header first 48 bytes
  remain unchanged. The 135 clone layouts have no collision in the captured
  native consumer's `(body variant, slot, A/B/C handle bank)` projection.
- Across 411 original Kits there are 3455 Pieces, 29 at most in one Kit.
  Original Piece types are 0/1/2 and slots are 0..9. Original Body header
  flags at +4 are zero. None has a projected handle-bank collision.
- Of 3455 original Pieces, 137 have all auxiliary bytes zero, **only**
  `(body=3, slot=1, type=0)` helmet entries. Every other original Piece has
  at least one nonzero auxiliary field.
- The clones introduce 931 Piece positions absent from their individual
  target's original `(body,slot,type)` layout. All 931 are zero-filled outside
  Unit, slot, type, weight and tone. Matching positions preserve target
  auxiliary bytes. None of the 931 belongs to the zero-aux original helmet
  position `(3,1,0)`; these zero-filled non-helmet entries are unprecedented
  in this complete original registry capture.
- The previously captured `game.dll+0x81f470` function reads Piece fields
  at +0x18/+0x20/+0x28/+0x48/+0x30/+0x40/+0x50. At +0x81faa0..0x81fad6
  it branches past auxiliary work when all seven are zero. This proves those
  fields affect a native consumer; it does **not** prove that branch crashes.
  The same function clears three banks of old instance handles and writes new
  handles by slot/type. A separate user's type2/type3 collision remains a
  distinct risk, absent from this local configuration.

## Interpretation and next controlled test

The strongest *structural* discrepancy is synthesized zero auxiliary metadata,
not Piece count (original maximum 29 versus 19), out-of-range slots, changed
identity, or handle-bank collisions in this configuration. The auxiliary values
may designate optional resources; their absence may be legitimate for some
custom Units. We cannot infer the semantics or the crash site from this
snapshot. The remote `helldivers2.exe+0x99d7cd / c0000005` event has no
stack, register state or dump, and may involve a different source layout.

For a controlled local diagnostic, preserve the same patch, target, Unit IDs
and layout, and compare a single target under the current zero-template clone
against a full 96-byte *matching native Piece donor* (changing only Unit and
target weight). The existing `test_piece_template` diagnostic code can support
that narrower B experiment if a donor with identical body/slot/type is found;
the custom Unit itself does not exist among original donors. This is not a
production fix. Record entering, remaining in and leaving the armory
separately. If the donor changes slot/type as well, the result cannot isolate
auxiliary metadata.
Do not copy unknown auxiliary hashes across unrelated slots or globally disable
the native consumer: either could alter resource ownership and make crashes
harder to attribute.
