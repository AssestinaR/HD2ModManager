---
name: hd2mod-safe-library-deletion
description: Inspect, preview, and safely delete Mod-library nodes with Option, Profile, activation, and generated-content safeguards.
---

# HD2 Mod Safe Library Deletion

Always preview first. Do not retry a blocked deletion by editing JSON manually.

```powershell
hd2mod mod delete preview <mod-id> [...] --library "<library>" --json
hd2mod mod delete preview <mod-id> [...] --with-unshared-options --library "<library>" --json
hd2mod mod delete <mod-id> [...] --manager-root "<manager>" --confirm --json
hd2mod mod delete <mod-id> [...] --with-unshared-options --manager-root "<manager>" --confirm --json
```

`--with-unshared-options` deletes recursive Options only when no surviving real host owns them. Shared Options are retained. Active decorations and temporary helmet-to-armor output deliberately block deletion until their managed overwrite is resolved in the desktop manager. Deletion never deploys automatically.
