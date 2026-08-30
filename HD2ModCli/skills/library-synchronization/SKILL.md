---
name: hd2mod-library-synchronization
description: Review and safely reconcile existing Mod-library Patch facts with library.json without importing, deleting, moving, or deploying Mods.
---

# HD2 Mod Library Synchronization

Use this Skill only when a Mod library directory and `library.json` disagree about existing top-level Patch content. It does **not** parse import packages, build an Option tree, delete leftovers, or deploy GameData.

```powershell
hd2mod library sync inspect --manager-root "<manager>" --library "<library>" --json
hd2mod library sync plan --manager-root "<manager>" --library "<library>" --output library-sync-plan.json --json
hd2mod library sync validate --manager-root "<manager>" --library "<library>" --plan library-sync-plan.json --json
hd2mod library sync apply --manager-root "<manager>" --library "<library>" --plan library-sync-plan.json --confirm --json
```

Always inspect, export a plan, review it, validate it immediately before apply, then pass `--confirm`. Apply only updates `library.json` node Patch facts: it preserves `profiles.json`, Option/Decoration activation state, source Patch files, and derived caches. New top-level Patch directories become plain Mod records with fixed IDs from the plan. A missing physical directory is blocking; use `safe-library-deletion` to remove its relationships safely, then make a new plan.
