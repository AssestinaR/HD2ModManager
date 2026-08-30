---
name: hd2mod-mod-management
description: Legacy routing note for HD2 Mod CLI metadata, Option, and safe-deletion capabilities.
---

# Legacy Mod Management Skill

Use one narrow capability instead:

- `mod-metadata` for names and notes.
- `option-management` for direct Option state and order.
- `safe-library-deletion` for previewed deletion.

This compatibility Skill intentionally duplicates no command reference. Run `hd2mod capabilities --json` before acting; none of the three capabilities deploys or edits Patch bytes.
