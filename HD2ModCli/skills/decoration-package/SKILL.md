---
name: hd2mod-decoration-package
description: Inspect source Units and publish a new external, importable Decoration package through a reviewed plan.
---

# HD2 Mod Decoration Package

Use this Skill to generate a portable Decoration package from one source Mod for one explicit target host. It only writes a new external package directory; it does not import it, enable it, rebuild `Overwrite`, or deploy GameData.

```powershell
hd2mod decoration inspect source <source-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod decoration package plan <source-id> --host <target-host-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --output decoration-plan.json --json
hd2mod decoration package validate --plan decoration-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod decoration package apply --plan decoration-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --output "<new-external-directory>" --confirm --json
```

Review `selections` and `attachment` in the plan before validating. The plan fixes source/target IDs and fingerprints every source base Patch plus `.stream` and `.gpu_resources`; validation rejects drift. Output must be a new directory outside the Mod library, GameData, and game root. Afterwards use `safe-import` to bring the generated package into the library; activation and generated `Overwrite` rebuilding are intentionally separate capabilities.
