---
name: hd2mod-patch-analysis
description: Directly inspect a Mod's physical Patch groups, AssetKeys, references, and Unit/geometry facts without writing cache, SQLite, library state, or GameData.
---

# HD2 Mod Patch Analysis

Use this before choosing a Patch for reconstruction, repackaging, cross-armor, decoration, or helmet merge. It is expensive for large model Mods but is strictly read-only.

```powershell
hd2mod mod analyze <mod-id> --library "<library>" --json
```

The result is grouped by physical `patchFileName`, `archiveHex16`, and `patchIndex`. Each entry is identified by `entryIndex` plus hexadecimal `assetKey`; output includes exact TOC/Stream/GPU sizes, group-level AssetKey-scope references, and a Unit's parsed meshes, material slots, geometry counts, and read error when partial decoding fails. `referenceScope` is `asset-key`: duplicate AssetKeys in one Patch cannot safely be attributed to a single entry.

The command directly calls `PatchGroupAnalyzer` at `full` depth. It does not use `ModInformationCenter`, does not write `data/indexes/mod-facts.sqlite`, does not populate manager caches, and never modifies Patch files, Profiles, options, deployment, or GameData. Inspect `diagnostics` before treating a partial result as a source for an output tool.
