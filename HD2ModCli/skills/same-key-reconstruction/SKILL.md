---
name: hd2mod-same-key-reconstruction
description: Inspect and output a fixed-source Same-key Canonical Patch reconstruction without modifying the Mod library.
---

# HD2 Mod Same-key Reconstruction

Use only to produce a candidate Patch for one registered Mod and one explicit source base Patch filename. It is not an in-place repair, import, or deployment workflow.

```powershell
hd2mod patch repair inspect <mod-id> [base.patch_N] --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod patch repair plan <mod-id> <base.patch_N> --manager-root "<manager>" --library "<library>" --game-data "<data>" --output same-key-plan.json --json
hd2mod patch repair validate --plan same-key-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod patch repair apply --plan same-key-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --output "<new-external-directory>" --confirm --json
```

For multi-Patch Mods, inspect without the optional filename and choose a listed `availableSourcePatchFileNames` value; do not pass a path. Validation and apply recheck source-family and GameData fingerprints plus Canonical eligibility. The result remains outside the library; import and deploy it separately if desired.
