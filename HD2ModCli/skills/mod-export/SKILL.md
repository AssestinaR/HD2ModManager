---
name: hd2mod-mod-export
description: Safely export one registered Mod and its recursive Option tree as a portable zip with manifest.json.
---

# HD2 Mod Export

Use this to publish a portable copy of an existing library Mod tree. It never changes the library or deploys GameData.

```powershell
hd2mod mod export plan <root-mod-id> --manager-root "<manager>" --library "<library>" --output mod-export-plan.json --json
hd2mod mod export validate --plan mod-export-plan.json --manager-root "<manager>" --library "<library>" --json
hd2mod mod export apply --plan mod-export-plan.json --manager-root "<manager>" --library "<library>" --output "<external-directory>" --confirm --json
```

The plan freezes `library.json`, the recursive node tree, and every top-level source file that the exporter will copy. Apply writes a private temporary zip first and publishes it only after a second source check. Use `--overwrite` only when replacing the expected same-name zip is intentional. Output directories inside the Mod library are rejected; manager-generated `Overwrite` directories are not exported.
