---
name: hd2mod-host-derived-overwrite
description: Safely enable or disable decorations or temporary helmet-to-armor merge on a host Mod's single managed Overwrite.
---

# HD2 Mod Host-Derived Overwrite

Use this only for a library Mod's manager-generated `Overwrite`. It never writes GameData and refuses a user-owned `Overwrite` without a manager marker.

```powershell
hd2mod host-derived inspect <host-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --json

hd2mod host-derived plan decoration enable <decoration-id> --host <host-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --output host-derived-plan.json --json
hd2mod host-derived plan decoration enable-all <decoration-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --output host-derived-plan.json --json
hd2mod host-derived plan helmet enable <host-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --output host-derived-plan.json --json
hd2mod host-derived plan rebuild <host-id> --manager-root "<manager>" --library "<library>" --game-data "<data>" --output host-derived-plan.json --json
hd2mod host-derived validate --plan host-derived-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --json
hd2mod host-derived apply --plan host-derived-plan.json --manager-root "<manager>" --library "<library>" --game-data "<data>" --confirm --json
```

Use `disable` / `disable-all` for the inverse. Review `current` versus `planned` for every host. Apply rebuilds every affected host first, then commits only `decoration-activations.json` or `library.json`; a failure restores already rebuilt hosts to their plan-time state.
