---
name: hd2mod-patch-resource-repackaging
description: Repackage reviewed whole AssetKey resources from one or more Mods into a new standalone Patch.
---

# HD2 Mod Patch Resource Repackaging

Use this only when the requested result is a new standalone Patch assembled from existing complete AssetKey payloads. It does not reconstruct meshes, edit bytes, import the result, or deploy it.

```powershell
hd2mod patch repackage inspect <mod-guid> [...] --library "<library>" --json
hd2mod patch repackage plan <mod-guid> [...] --library "<library>" --output .\repackage.plan.json --json
hd2mod patch repackage validate --library "<library>" --plan .\repackage.plan.json --json
hd2mod patch repackage apply --library "<library>" --plan .\repackage.plan.json --output .\output\repacked --confirm --json
```

The plan defaults to every non-conflicting candidate. For a true AssetKey conflict, choose exactly one existing `variantId` and one of its existing `patchGroupId` values in `selections`, then validate again. Never invent IDs or select two variants for one AssetKey.

`apply` rescans hashes and payload fingerprints, rejects drift, and writes only to a new directory outside the Mod library. Treat any blocking diagnostic as a stop condition; use the normal import and deployment Skills separately if the output should later be managed or used in GameData.
