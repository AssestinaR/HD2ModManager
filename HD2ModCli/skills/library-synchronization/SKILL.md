---
name: hd2mod-library-synchronization
description: Review and safely reconcile existing Mod-library Patch facts with library.json without importing, deleting, moving, or deploying Mods.
---

# HD2 Mod Library Synchronization

Use this only when existing top-level library directories and `library.json` disagree about Patch facts. It does not parse import packages, build an Option tree, delete leftovers, or deploy GameData.

```powershell
hd2mod library sync inspect --manager-root "<manager>" --library "<library>" --json
hd2mod library sync plan --manager-root "<manager>" --library "<library>" --output library-sync-plan.json --json
hd2mod library sync validate --plan library-sync-plan.json --manager-root "<manager>" --library "<library>" --json
hd2mod library sync apply --plan library-sync-plan.json --manager-root "<manager>" --library "<library>" --confirm --json
```

Review the exported JSON plan and validate it immediately before apply. Apply holds the library write lease and updates only `library.json` Patch facts; it preserves Profiles, activation files, source Patch files, and caches. A missing registered directory is blocking: use `safe-library-deletion`, then create a fresh plan.
