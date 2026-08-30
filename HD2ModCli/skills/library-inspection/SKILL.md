---
name: hd2mod-library-inspection
description: Inspect an HD2 Mod Manager library, Mod relationships, activation state, Profiles, and effective deployment order without mutation.
---

# HD2 Mod Library Inspection

Use this Skill only for read-only library questions. It never imports, deletes, moves, deploys, or analyzes packages.

```powershell
hd2mod capabilities --json
hd2mod status --manager-root "<manager>" --json
hd2mod mod list --library "<library>" --json
hd2mod mod show <mod-guid-or-unique-prefix> --library "<library>" --json
hd2mod profile show [name-or-guid-prefix] --library "<library>" --json
hd2mod context export --library "<library>" --output context.json
```

Treat exported context as a point-in-time snapshot. Read `directOptions`, host-scoped `optionActivation`, `logicalEntries`, and `effectiveDeploymentEntries`; never guess an ambiguous GUID prefix. This Skill does not authorize a mutation, deployment, or Patch operation.
