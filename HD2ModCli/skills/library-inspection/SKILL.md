---
name: hd2mod-library-inspection
description: Inspect an HD2 Mod Manager library, Mod relationships, activation state, Profile content, and effective deployment order without changing anything.
---

# HD2 Mod Library Inspection

Use only for read-only questions. It does not authorize import, deletion, deployment, Profile edits, or Patch operations.

```powershell
hd2mod capabilities --json
hd2mod status --manager-root "<manager directory>" --json
hd2mod context export --manager-root "<manager directory>" --output context.json
hd2mod mod show <guid-or-unique-prefix> --library "<mod library>" --json
hd2mod profile show [name-or-guid-prefix] --library "<mod library>" --json
```

Treat `context.json` as a point-in-time snapshot. For Options, read host-scoped `optionActivation` and relationship IDs; for Profiles, distinguish `logicalEntries` from `effectiveDeploymentEntries`. Never guess an ambiguous GUID prefix or repair malformed library files by hand.
