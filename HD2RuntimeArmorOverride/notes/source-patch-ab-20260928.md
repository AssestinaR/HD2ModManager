# Native source mapping, patch absent/present A/B (2026-09-28)

## Setup

- The same protocol revision 65 mapped one target, RE-824 (4259903063),
  to the original RE-2310 armor Kit (820333399). No custom model was used.
- A was launched without the RE-2310 replacement package. B added only
  library node `09508619-0c84-4f28-b813-389b0ea175fa`
  (`Chiffon-LUNALICE` for RE-2310) to the active profile, then redeployed.
- The existing read-only registry-slot snapshot addon was deployed in both
  arms. No gameplay or visual observation was made. Thus this test establishes
  the runtime registry comparison, not the reported transparency outcome.
- Complete evidence is in `../evidence/source-patch-ab-20260928/`:
  `absent-25580.jsonl` and `present-81704.jsonl`, each with one complete
  start/end pair, 411 original Kit records and one published clone.

## Byte comparison

Comparing Kit headers before their relocated Body pointer/count, Body headers
before relocated Piece pointer/count, and all 96 bytes of every Piece:

- A and B have **zero canonical byte differences** across all 412 captured
  records (411 originals + one published clone). Revision and mapping count
  were identical. Raw pointer addresses differ by process and are excluded.
- Original RE-2310 has Body kinds 3/0/1 with 7/8/8 Pieces. The RE-824 clone
  has the same Body order and all 23 source Pieces in the same order.
  For every Piece, the only changed byte versus RE-2310 is byte +16
  (target-specific weight). Unit, slot, type, all eight auxiliary references,
  tone and remaining bytes are retained.
- The first 48 bytes of the clone Kit header equal the target RE-824 header,
  not the source RE-2310 header. This intentionally retains the target item
  identity and target Kit archive hash (`f50c7fd427839c47` versus the source
  Kit header archive hash `d923906f8b815987`).
- Canonical SHA-256 values in both arms:
  source original `4745ae776de21caa2b8b21ee8b8e6e843e834a86347cc722cf60f227f64bc9b7`;
  target original `a2742942a83e3456efb55d463d86730b0051b6042476e3d9f8d0b2d58fff5afe`;
  target published `1c0bcc4d1322a93cb3ec8c613fb215124c3442d4e67c9da33442bdbb44ddcee9`.
- CLI patch inspection reports 23 replaced Unit assets in the package.
  Their IDs intersect 22 of the 23 RE-2310 source Piece Unit IDs. The one
  absent Unit, `3c33cf10a26cbb3e`, is body 3, slot 1, type 0, with zero
  auxiliary fields. The remaining replacement is a helmet Unit.
  The deployed override graph identified a package-owned winner for the
  checked visible RE-2310 Unit `5de0cf947d0e6520`.

## Interpretation

This A/B rules out a different or truncated **published registry composition**
as the direct explanation of an appearance difference between the two package
states. It does not prove why an unpatched source is transparent: no visual
observation was collected in either run. Resource loading, lookup scope,
dependency availability or instantiated objects remain hypotheses. In
particular, retaining the target Kit header archive while moving source
Pieces warrants investigation, but archive-scoped lookup is not yet proved.

The user's earlier CW-36 -> FS-23 observation is separate. This experiment
uses RE-2310 -> RE-824 and its patch; do not substitute its results for a
CW-36 runtime capture or claim the original transparency reproduced here.

The active profile and protocol were byte-for-byte restored from pretest
backups; the original profile was validated and redeployed. The probe and
RE-2310 replacement node are no longer in the active profile.
