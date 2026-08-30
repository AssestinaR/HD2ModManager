---
name: hd2mod-cli-router
description: Discover and export one narrowly scoped HD2 Mod Manager CLI Skill for the requested task.
---

# HD2 Mod CLI Router

Discover the current command surface, then export only the needed capability Skill:

```powershell
hd2mod capabilities --json
hd2mod agent skills list --json
hd2mod agent skills export <skill-name> --output .\.agents\skills
```

Use `agent skills export --output <directory>` to export all capability Skills. `agent skill --output SKILL.md` is only the compatibility router export.

- Read state: `library-inspection`.
- Reconcile library facts: `library-synchronization`.
- Import one source: `safe-import`; import a reviewed source collection: `batch-import`.
- Read actual Patch contents before choosing advanced operations: `patch-analysis`.
- Rename, notes, images, Options, deletion, or Profiles: `mod-metadata`, `mod-media`, `option-management`, `safe-library-deletion`, `profile-management`, `profile-alignment`.
- Create a decoration package: `decoration-package`.
- Deploy or deactivate GameData: `safe-deployment`.
- Inspect original GameData mappings or actual Data-Patch winners: `gamedata-inspection`.
- Rebuild Manager-owned GameData indexes or Armor / Helmet Unit facts: `gamedata-index-management`.
- Manage a host Mod's generated Overwrite: `host-derived-overwrite`.
- Produce a standalone Patch: `patch-resource-repackaging`, `helmet-to-armor-merge`, `cross-armor-transfer`, or `same-key-reconstruction`.

Use `--json` for Agent-facing calls. IDs are explicit GUIDs or unique prefixes; never guess an ambiguous selector. Plan export writes only its named output, validation is read-only, and every mutating `apply` requires explicit `--confirm`. `safe-deployment` is the only Skill that authorizes GameData writes. The standalone Patch Skills output fresh external directories and do not import, enable, or deploy their results. `mod-management` is a legacy aggregate pointer, not a preferred workflow.
