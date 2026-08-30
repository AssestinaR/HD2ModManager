---
name: hd2mod-patch-resource-repackaging
description: Repackage reviewed whole AssetKey resources from one or more Mods into a new standalone Patch.
---

# HD2 Mod Patch Resource Repackaging

Use only to assemble a new standalone Patch from existing complete AssetKey payloads. It does not reconstruct meshes, edit source bytes, import, or deploy.

```powershell
hd2mod patch repackage inspect <mod-id> [...] --library "<library>" --json
hd2mod patch repackage plan <mod-id> [...] --library "<library>" --output repackage-plan.json --json
hd2mod patch repackage validate --plan repackage-plan.json --library "<library>" --json
hd2mod patch repackage apply --plan repackage-plan.json --library "<library>" --output "<new-external-directory>" --confirm --json
```

The plan selects non-conflicting candidates by default. For a real AssetKey conflict, select exactly one existing `variantId` and one existing `patchGroupId`; never invent IDs or select two variants for one key. Apply rechecks hashes and payload fingerprints, then writes only to a fresh directory outside the Mod library.
