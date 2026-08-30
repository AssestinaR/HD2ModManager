---
name: hd2mod-gamedata-inspection
description: Read original GameData Archive mappings and actual Data-Patch override winners without writing the index, Mod library, or game files.
---

# HD2 Mod GameData Inspection

Use `archive`/`asset` for the persisted original GameData index. Use `override` for the actual top-level Patch winners in the selected Data directory. They are independent facts.

```powershell
hd2mod gamedata status --manager-root "<manager>" --game-data "<data-or-copy>" --json
hd2mod gamedata archive list --manager-root "<manager>" --game-data "<data-or-copy>" --limit 100 --json
hd2mod gamedata archive show <archive-id> --manager-root "<manager>" --game-data "<data-or-copy>" --with-effective --json
hd2mod gamedata asset find 0x<TypeID> 0x<FileID> --manager-root "<manager>" --game-data "<data-or-copy>" --json

hd2mod gamedata override status --game-data "<data-or-copy>" --json
hd2mod gamedata override list --game-data "<data-or-copy>" --only-competition --limit 100 --json
hd2mod gamedata override show 0x<TypeID> 0x<FileID> --game-data "<data-or-copy>" --json
```

Asset IDs are fixed 16-digit hexadecimal strings in output; never convert them to JavaScript numbers. Follow `nextCursor` exactly for the next page. Only treat archive mappings as usable when `index.mappingTrusted` is true, and only use override results for future write planning when `trusted` is true. `gamedata browser` is an explicit, expensive UI-parity diagnostic; it scans library Patch facts directly but never writes derived caches.
