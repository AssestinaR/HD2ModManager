---
name: hd2mod-unit-inspection
description: Read-only Unit data view — parse a Unit inside any Patch and read its mesh/stream/vertex components, geometry facts, and bone table without registering a Mod.
---

# HD2 Mod Unit Inspection

Read-only structural view of a **Unit** inside any Patch file. These commands wrap the base-layer
Unit parser, so an external script no longer has to re-implement Unit decoding (TOC + GPU payloads,
vertex components, bone tables) in its own language.

Every command here:

- works on **any Patch file path** — it is **not** required to be a registered Mod;
- is **strictly read-only**: it rejects `--output` / `--overwrite` / `--plan` / `--confirm` and writes nothing;
- selects Units with `--keys` (inline `0xtypeid:0xfileid,...`, `@keys.json`, or a literal JSON array).

## 1. Structure overview

```powershell
hd2mod unit show "<patch.patch_0>" --keys 0xe0a48d0be9a7453f:0x<fileid> --json
```

Reports version, name hash, bone groups, customization (body type / slot / weight / piece type),
every **stream** with its vertex count, stride, index count and **vertex component list**
(`position`, `normal`, `uv`, `bone_weight`, `bone_index`, ...), and every **mesh** with its mesh id,
LOD index, material/section counts and semantic name.

## 2. Geometry facts

```powershell
hd2mod unit geometry "<patch.patch_0>" --keys 0xe0a48d0be9a7453f:0x<fileid> --json
```

Per mesh: vertex count, triangle count, whether it is a visual / culling mesh, and the geometry
quality class — `Unreadable` / `Placeholder` / `CullingOnly` / `Renderable` / `RenderableLod0`.
Also reports `visibleMeshCount`, `hasRenderableVisibleGeometry` and `isFullyHidden`.

Use this to tell a **deliberately hidden placeholder Unit** apart from one that still carries a
visible model.

## 3. Bone table

```powershell
hd2mod unit bones "<patch.patch_0>" --keys 0xe0a48d0be9a7453f:0x<fileid> --json
```

Per bone group: `numBones`, the real bone indices and the remap count. When the same Patch carries a
Bones resource, its **bone name table** is included as well.

## Composing a workflow

```powershell
# 1. Which Units does this Patch carry?
hd2mod patch entries list "<patch.patch_0>" --json

# 2. Is the model actually visible, or a hidden placeholder?
hd2mod unit geometry "<patch.patch_0>" --keys 0xe0a48d0be9a7453f:0x<fileid> --json

# 3. What does the mesh look like, and which bones does it use?
hd2mod unit show  "<patch.patch_0>" --keys 0xe0a48d0be9a7453f:0x<fileid> --json
hd2mod unit bones "<patch.patch_0>" --keys 0xe0a48d0be9a7453f:0x<fileid> --json
```

## Notes

- `--keys` accepts exactly the same three forms as the other primitive commands.
- A key whose type is not `unit` (`0xe0a48d0be9a7453f`) is rejected before any file is touched.
- The Unit parser resolves Composite and Bones dependencies from the **same Patch**; an external
  reference is reported rather than followed.
- This is a **view**, not an editor. To modify a Unit, use `patch edit` / `patch hide-unit`.
