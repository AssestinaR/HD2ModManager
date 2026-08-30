---
name: hd2mod-safe-library-deletion
description: Preview and delete explicit Mod-library nodes with Option, Profile, activation, and generated-content safeguards.
---

# HD2 Mod Safe Library Deletion

Use the plan workflow for Agent-driven deletion. Do not bypass a blocked result by editing library JSON.

```powershell
hd2mod mod delete plan <mod-id> [...] --manager-root "<manager>" --output deletion-plan.json --json
hd2mod mod delete validate --plan deletion-plan.json --manager-root "<manager>" --json
hd2mod mod delete apply --plan deletion-plan.json --manager-root "<manager>" --confirm --json

# Optional recursive scope; it is frozen into the plan.
hd2mod mod delete plan <mod-id> [...] --with-unshared-options --manager-root "<manager>" --output deletion-plan.json --json
```

The plan fingerprints the library, Profiles, activation files, and every target storage directory. Validate/apply reject drift. `--with-unshared-options` removes recursive Options only when no surviving real host owns them. Active Decorations and managed temporary helmet-to-armor output block deletion. This capability never deploys GameData. `mod delete preview` and direct `mod delete` remain compatibility shortcuts, not the preferred Agent workflow.
