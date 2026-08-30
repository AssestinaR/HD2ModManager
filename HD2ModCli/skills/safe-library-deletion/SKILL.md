---
name: hd2mod-safe-library-deletion
description: Preview and delete explicit Mod-library nodes with Option, Profile, activation, and generated-content safeguards.
---

# HD2 Mod Safe Library Deletion

Always preview first. Do not bypass a blocked result by editing library JSON manually.

```powershell
hd2mod mod delete preview <mod-id> [...] --library "<library>" --json
hd2mod mod delete preview <mod-id> [...] --with-unshared-options --library "<library>" --json
hd2mod mod delete <mod-id> [...] --manager-root "<manager>" --confirm --json
hd2mod mod delete <mod-id> [...] --with-unshared-options --manager-root "<manager>" --confirm --json
```

`--with-unshared-options` removes recursive Options only when no surviving real host owns them. Active decorations and temporary helmet-to-armor output block deletion. It never deploys automatically.
