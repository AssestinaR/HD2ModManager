---
name: hd2mod-same-key-reconstruction
description: Inspect and output a fixed-source Same-key Canonical Patch reconstruction without modifying the Mod library.
---

# HD2 Mod Same-key Reconstruction

Use this only to produce a new candidate Patch for one registered Mod and one explicitly selected base Patch file. It is not an in-place repair, import, or deployment workflow.

```powershell
hd2mod patch repair inspect <mod-guid> [base.patch_N] --manager-root "<manager>" --game-data "<game-data>" --json
hd2mod patch repair plan <mod-guid> <base.patch_N> --manager-root "<manager>" --game-data "<game-data>" --output .\same-key.plan.json --json
hd2mod patch repair validate --plan .\same-key.plan.json --manager-root "<manager>" --game-data "<game-data>" --json
hd2mod patch repair apply --plan .\same-key.plan.json --manager-root "<manager>" --game-data "<game-data>" --output .\output\same-key --confirm --json
```

If a Mod has multiple base Patch files, first inspect without the optional file name and choose one from `availableSourcePatchFileNames`. Never guess or pass a path: the command accepts only a base Patch filename belonging to that Mod.

`validate` and `apply` recheck the source Patch family, GameData index fingerprint, and Core's Canonical eligibility. Treat a blocking diagnostic as a stop condition. The result stays outside the library; use the normal import and deployment Skills separately if it should later be managed or used.
