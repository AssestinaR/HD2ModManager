---
name: hd2mod-gamedata-extraction
description: Read and extract assets straight out of the installed Helldivers 2 game packages without the SQLite index or a manager installation.
---

# HD2 Mod GameData Extraction

`gamedata extract` reads the game packages themselves. It needs no `--manager-root`, no
persisted asset index, and it never writes to GameData, the Mod library, or derived caches.
Use it when the authoritative bytes of an original asset are required: reverse-engineering
formats, building or repairing a Mod's Patch payload, or verifying what the game actually
ships. Use `gamedata archive`/`asset find` instead when the question is about the indexed
mapping, and `gamedata override` when the question is about deployed Patch winners.

```powershell
# 1. Enumerate. <type> accepts a typehash name, a 0x hash, a comma list, or `all`.
hd2mod gamedata extract list all --game-data "<data>" --limit 100 --json
hd2mod gamedata extract list bones --game-data "<data>" --json
hd2mod gamedata extract list unit 18235e0c9ec0e636 --game-data "<data>" --json

# 2. Inspect one asset's archive and part sizes without writing anything.
hd2mod gamedata extract show 0x<TypeID> 0x<FileID> --game-data "<data>" --json

# 3. Extract one asset (toc / stream / gpu_resources) into a directory.
hd2mod gamedata extract asset 0x<TypeID> 0x<FileID> --game-data "<data>" --output "<dir>" --json

# 4. Extract an entire archive, grouped into one folder per asset type.
hd2mod gamedata extract archive <archive-id> --game-data "<data>" --output "<dir>" --json
```

Rules that matter:

- Type and file IDs are fixed 16-digit hexadecimal strings in output; never convert them to
  JavaScript numbers. Pass them back verbatim.
- `list` is paged: follow `nextCursor` exactly, and keep `--limit` between 1 and 500.
- The same `(type, file)` pair can exist in more than one archive. `asset`/`show` refuse to
  guess and report `GameDataAssetAmbiguous`; append the archive ID as the last positional
  argument to disambiguate.
- `--output` must never sit inside, or contain, the game `data` directory; the command refuses
  with `OutputInsideGameData`. A non-empty output directory requires an explicit `--overwrite`.
- `extract archive` writes `extract-manifest.json` at the output root describing every asset
  and every file it wrote. Read that instead of parsing standard output.
- Extraction is read-only with respect to the game and the Mod library. It is not a plan/apply
  workflow, so there is no `--confirm`, `--plan`, `--manager-root`, or `--library`.
