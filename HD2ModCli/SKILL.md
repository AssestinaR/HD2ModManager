---
name: hd2mod-cli-router
description: Route an Agent to one small HD2 Mod Manager CLI Skill.
---

# HD2 Mod CLI Router

Discover the shipped capability Skills, then export only the one required:

```powershell
hd2mod capabilities --json
hd2mod agent skills list --json
hd2mod agent skills export <skill-name> --output .\.agents\skills
```

- `library-inspection` — read-only library, Option and Profile inspection.
- `safe-import` — reviewed source import.
- `mod-metadata` — Mod name and notes.
- `option-management` — direct Option state and order for one host.
- `safe-library-deletion` — previewed relationship-aware deletion.
- `profile-management` — Profile lifecycle and logical order.
- `safe-deployment` — audited GameData deployment or tracked deactivation.
- `patch-resource-repackaging` — audited whole-entry resource selection into a standalone Patch.

Do not load every Skill by default. `mod-management` is a legacy aggregate only. `safe-deployment` is the only Skill that authorizes GameData writes. `patch-resource-repackaging` only writes a new library-external output directory; neither Skill authorizes Patch reconstruction, armor replacement, or arbitrary Patch-byte editing.
