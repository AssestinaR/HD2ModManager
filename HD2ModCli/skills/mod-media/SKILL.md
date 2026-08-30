---
name: hd2mod-mod-media
description: Inspect or safely update the convention-based icon image of one existing Mod without touching Patch or deployment state.
---

# HD2 Mod Media

```powershell
hd2mod mod image show <mod-id> --library "<library>" --json
hd2mod mod image set <mod-id> "<image.png>" --manager-root "<manager>" --confirm --json
hd2mod mod image clear <mod-id> --manager-root "<manager>" --confirm --json
```

`set` accepts PNG, JPG, JPEG, WEBP, or BMP and atomically replaces only conventional `icon.*` names. `clear` removes only those conventional names; it preserves any unrelated author image assets. The manager must be closed. Neither command changes Patch bytes, Options, Profiles, or GameData deployment.
