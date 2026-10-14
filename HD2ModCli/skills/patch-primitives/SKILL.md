---
name: hd2mod-patch-primitives
description: File-in/file-out Patch primitives — enumerate, export, compose, edit, and hide Units in any Patch file without registering a Mod.
---

# HD2 Mod Patch Primitives

Use these to assemble custom Patch workflows the desktop Manager cannot express directly. Every command here:

- works on **any Patch file path** (external, vanilla extraction, or downloaded) — it is **not** required to be a registered Mod;
- **never modifies its source** and never touches the Mod library or GameData;
- writes only into a **fresh `--output` directory** supplied by you;
- takes **no `--plan` / `--confirm` / lease** — it is a stateless transform.

If you need to operate on a registered Mod's Patch groups instead, use `hd2mod patch repackage` (which does require the Mod to be registered). Use this skill when the source is a bare file path or when you need entry-level surgery.

## 1. Look inside any Patch

```powershell
hd2mod patch entries list "<patch.patch_0>" --json
hd2mod patch entry show "<patch.patch_0>" 0x<typeid> 0x<fileid> --json
```

`entries list` reports every entry with its type id, file id, semantic `kind` (`unit` / `bone` / `composite-unit` / `asset`) and the three payload sizes (`tocDataSize` / `streamSize` / `gpuResourceSize`). This is the starting point of any external script.

## 2. Export one entry's three raw segments

```powershell
hd2mod patch entry export "<patch.patch_0>" 0x<typeid> 0x<fileid> --output "<dir>" [--overwrite] --json
```

Writes `toc.bin`, `stream.bin`, `gpu_resources.bin` and `entry.json` under `<dir>/0x<typeid>_0x<fileid>/`. This is the "unpack a Patch into one discrete file per AssetKey" primitive.

## 3. Compose a new Patch from multiple sources

```powershell
hd2mod patch compose --header "<header-template.patch_0>" \
  --pick "<src-a.patch_0>" 0x<typeid> 0x<fileid> \
  --pick "<src-b.patch_0>" 0x<typeid> 0x<fileid> \
  --output "<new-dir>" [--overwrite] --json
```

`--header` supplies the TOC header and type table; every header-template entry is discarded, and only the `--pick`ed entries are written. Sources may be different files and may not repeat an AssetKey. This is the standalone form of the multi-source merge the Manager performs internally when folding a helmet archive into an armor archive.

## 4. Entry-level surgery

```powershell
hd2mod patch edit "<src.patch_0>" --remove-key 0x<typeid>:0x<fileid> --output "<new-dir>" [--overwrite] --json
hd2mod patch edit "<src.patch_0>" --keys <unit-keys>          --output "<new-dir>" [--overwrite] --json
```

- `--remove-key` drops entries (repeatable, comma-separated).
- `--add-key` copies an existing entry in the same Patch (currently limited to entries already present).
- `--keys` hides Unit entries in place while keeping every other entry: it builds a complete new Patch from the source with those Units replaced by invisible canonical placeholders.

## 5. Hide Units into a standalone Patch

```powershell
hd2mod patch hide-unit "<src.patch_0>" --keys "<0xtype:0xfile>[,<...>]" --output "<new-dir>" [--overwrite] --json
hd2mod patch hide-unit "<src.patch_0>" --keys @capes.json --output "<new-dir>" --json
```

The output Patch contains **only** the hidden Units, retaining each Unit's bone / material / stream declarations. `--keys` accepts inline `0xtype:0xfile` pairs, `@file.json` (a JSON array or an object with a `keys` array — e.g. the output of `gamedata equipment list`), or a literal JSON string.

## Composing an end-to-end workflow

```powershell
# 1. Which Unit AssetKeys are capes? (game authority table, read-only)
hd2mod gamedata equipment list --game-data "<game data>" --kind cape --json > capes.json

# 2. Extract the vanilla archive that owns those Units, then hide them.
hd2mod patch hide-unit "<vanilla.patch_0>" --keys @capes.json --output "./capes-hidden"

# 3. Import the result only when the human is ready.
hd2mod import plan "./capes-hidden" --manager-root "<manager>" --output import-plan.json --json
```

Every intermediate artifact is a plain file you can inspect; nothing reaches the library until you explicitly run `import`.
